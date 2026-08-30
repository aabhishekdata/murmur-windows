using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Dictionary;
using Murmur.Speech;

namespace Murmur.App;

/// <summary>
/// Wires the app together: settings, storage, the platform layer, and the engine.
/// </summary>
/// <remarks>
/// <para>
/// The only place that knows about every layer at once. Views take what they need; nothing
/// else reaches across.
/// </para>
/// <para>
/// The platform implementations are resolved by reflection rather than referenced directly,
/// so <c>Murmur.App</c> can target plain <c>net10.0</c> and therefore be built, run and
/// headless-tested on macOS — which is the entire reason Avalonia was chosen over WPF. On any
/// non-Windows machine the lookup simply finds nothing and the app runs with inert stand-ins,
/// which is exactly what a UI test wants.
/// </para>
/// </remarks>
public sealed class Composition : IAsyncDisposable
{
    private Composition(
        AppSettings settings,
        DictionaryFile dictionary,
        SnippetStore snippets,
        TranscriptStore transcripts,
        DictationEngine? engine,
        IAudioDeviceCatalog? microphones,
        bool platformAvailable)
    {
        Settings = settings;
        Dictionary = dictionary;
        Snippets = snippets;
        Transcripts = transcripts;
        Engine = engine;
        Microphones = microphones;
        IsPlatformAvailable = platformAvailable;
    }

    /// <summary>User preferences.</summary>
    public AppSettings Settings { get; }

    /// <summary>The correction dictionary.</summary>
    public DictionaryFile Dictionary { get; }

    /// <summary>Voice-triggered text expansions.</summary>
    public SnippetStore Snippets { get; }

    /// <summary>Transcript history.</summary>
    public TranscriptStore Transcripts { get; }

    /// <summary>The microphone list, or null when no platform layer is available.</summary>
    public IAudioDeviceCatalog? Microphones { get; }

    /// <summary>The dictation engine, or null when no platform layer is available.</summary>
    public DictationEngine? Engine { get; }

    /// <summary>Whether real audio and hotkey support were found.</summary>
    public bool IsPlatformAvailable { get; }

    /// <summary>Whether the speech model is present on disk.</summary>
    public static bool IsModelInstalled => ParakeetTranscriber.Locate() is not null;

    /// <summary>
    /// Builds a graph over storage supplied by the caller, with no engine behind it.
    /// </summary>
    /// <param name="settings">Preferences, usually pointed at a scratch directory.</param>
    /// <param name="dictionary">The dictionary file.</param>
    /// <param name="transcripts">Transcript history.</param>
    /// <param name="snippets">Snippets, or null for an empty store beside the settings file.</param>
    /// <param name="microphones">A stand-in microphone list, or null for none.</param>
    /// <returns>A composition suitable for rendering the UI.</returns>
    /// <remarks>
    /// For headless previews and UI tests. The views take a <see cref="Composition"/>, and the
    /// alternative to this seam is either a test that writes into the user's real
    /// <c>%LocalAppData%</c> or a second, parallel construction path through every view — which
    /// is how a preview quietly stops resembling the app.
    /// </remarks>
    public static Composition ForPreview(
        AppSettings settings,
        DictionaryFile dictionary,
        TranscriptStore transcripts,
        SnippetStore? snippets = null,
        IAudioDeviceCatalog? microphones = null) =>
        new(settings,
            dictionary,
            snippets ?? new SnippetStore(Path.Combine(
                Path.GetDirectoryName(dictionary.FilePath) ?? Path.GetTempPath(), "snippets.jsonl")),
            transcripts,
            engine: null,
            microphones,
            platformAvailable: false);

    /// <summary>Builds the object graph.</summary>
    public static Composition Create()
    {
        var settings = new AppSettings(AppSettings.DefaultPath);
        var dictionary = new DictionaryFile(DictionaryFile.DefaultPath);
        var snippets = new SnippetStore(SnippetStore.DefaultPath);
        var transcripts = new TranscriptStore(TranscriptStore.DefaultPath);

        // The device ID is read through a closure rather than passed by value, so choosing a
        // different microphone in Settings or on the bar takes effect on the next dictation
        // instead of at the next launch.
        var capture = PlatformFactory.CreateAudioCapture(() => settings.Data.MicrophoneId);
        var hotkey = PlatformFactory.CreateHotkeySource(settings.Data.PushToTalkKey);
        var injector = PlatformFactory.CreateTextInjector();
        var microphones = PlatformFactory.CreateAudioDeviceCatalog();

        DictationEngine? engine = null;
        var available = capture is not null && hotkey is not null && injector is not null;

        if (available)
        {
            var modelDirectory = settings.Data.ModelDirectory ?? ParakeetTranscriber.Locate();

            ITranscriber transcriber = modelDirectory is not null
                ? new ParakeetTranscriber(modelDirectory)
                : new UnavailableTranscriber();

            engine = new DictationEngine(
                capture!, hotkey!, transcriber, injector!,
                () => dictionary.Entries,
                clock: null,
                // Read fresh per utterance, for the same reason the dictionary is: a toggle
                // flipped in Settings applies to the very next thing the user says.
                options: () => settings.Data.ToOptions() with { Snippets = snippets.Entries });

            // The recogniser must be loaded before it can transcribe; forgetting this call
            // was a shipped bug — TranscribeAsync silently returns "" while unloaded, so the
            // meter moved but no text ever appeared. LoadAsync is synchronous work behind an
            // async signature (constructing OfflineRecognizer reads ~660 MB of ONNX weights),
            // so it is pushed off the startup thread; a failure is logged rather than lost.
            var toLoad = transcriber;
            _ = Task.Run(async () =>
            {
                try
                {
                    await toLoad.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    CrashLog.Write("Speech model load failed", ex);
                }
            });

            // Completed fires on the engine's background processing thread (the chain starts
            // on the dedicated keyboard-hook thread and stays off the UI thread throughout).
            // TranscriptStore.Add raises Changed synchronously, and TranscriptionsView
            // rebuilds live controls from that event — which Avalonia only allows on the UI
            // thread. Calling Add directly from here was a shipped bug: the cross-thread
            // exception propagated back into the engine, was swallowed, and blocked text
            // injection. Post rather than Invoke, so the completion thread never blocks
            // waiting on the UI thread (which may be busy, or gone during shutdown).
            engine.Completed += (_, result) =>
            {
                if (!settings.Data.KeepHistory) return;

                var record = new TranscriptRecord
                {
                    At = result.At,
                    AudioSeconds = result.AudioDuration.TotalSeconds,
                    ProcessingSeconds = result.ProcessingTime.TotalSeconds,
                    Text = result.Text,
                    Corrections = result.Corrections.Count > 0 ? result.Corrections : null,
                };

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // On the UI thread an escaped exception takes down the whole
                    // tray-resident process, so a history failure is logged instead.
                    try
                    {
                        transcripts.Add(record);
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Write("Saving transcript failed", ex);
                    }
                });
            };
        }

        return new Composition(
            settings, dictionary, snippets, transcripts, engine, microphones, available);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Engine is not null) await Engine.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// A transcriber that reports the model is missing rather than throwing.
/// </summary>
/// <remarks>
/// A fresh Windows install has no model, and that has to be a readable message in Settings
/// rather than a crash on first press.
/// </remarks>
internal sealed class UnavailableTranscriber : ITranscriber
{
    public bool IsReady => false;

    public ValueTask<bool> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken) => ValueTask.FromResult(string.Empty);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
