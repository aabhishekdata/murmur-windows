using System.Diagnostics;
using Murmur.Abstractions;
using Murmur.App;
using Murmur.Cloud;
using Murmur.Core;
using Murmur.Platform.Windows;
using Murmur.Speech;
using Murmur.Testing;
using Xunit;

namespace Murmur.E2E.Tests;

/// <summary>What one dictation through the pipeline came to.</summary>
/// <param name="Clip">What was played into the microphone.</param>
/// <param name="Result">The completed dictation, or null when none completed.</param>
/// <param name="Fault">What <see cref="DictationEngine.TranscriptionFaulted"/> reported, or null.</param>
/// <param name="Keyboard">Every keystroke and clipboard operation, in order.</param>
/// <param name="Typed">Everything typed with <c>SendInput</c> Unicode, joined.</param>
/// <param name="Pasted">What was on the clipboard when the paste chord went, or null when nothing was pasted.</param>
/// <param name="Wall">Key release to the engine back at rest, typing included.</param>
internal sealed record Dictation(
    Clip Clip,
    DictationResult? Result,
    Exception? Fault,
    IReadOnlyList<string> Keyboard,
    string Typed,
    string? Pasted,
    TimeSpan Wall)
{
    /// <summary>What reached the focused app: the pasted text, or else the typed text.</summary>
    public string Delivered => Pasted ?? Typed;

    /// <summary>Whether Enter was pressed after the text landed.</summary>
    public bool PressedEnterAfterText
    {
        get
        {
            var enter = Keyboard.ToList().LastIndexOf("enter");
            var text = Keyboard.ToList().FindLastIndex(e => e == "paste" || e.StartsWith("type:", StringComparison.Ordinal));
            return enter >= 0 && text >= 0 && enter > text;
        }
    }

    /// <summary>Whether Enter was pressed at all.</summary>
    public bool PressedEnter => Keyboard.Contains("enter");
}

/// <summary>
/// The app's dictation pipeline, assembled exactly as <see cref="Composition.Create"/> assembles it,
/// with only the operating system's edges replaced.
/// </summary>
/// <remarks>
/// <para>
/// Real: <see cref="DictationEngine"/>, <see cref="RoutingTranscriber"/> with its
/// <see cref="FallbackTranscriber"/>, <see cref="LazyTranscriber"/> around a real
/// <see cref="ParakeetTranscriber"/>, <see cref="AzureOpenAITranscriber"/> over the real
/// <see cref="HttpClient"/> from <see cref="AzureHttp"/>, <see cref="SmartTextInjector"/>, the text
/// pipeline with a saved prompt and a dictionary, <see cref="AppSettings"/>, and the key in a
/// <see cref="ProtectedSecretFile"/> encrypted with the real DPAPI protector — all in a scratch
/// folder of its own.
/// </para>
/// <para>
/// Replaced: the microphone (a WAV file, <see cref="WavFileAudioCapture"/>), the push-to-talk key
/// (<see cref="FakeHotkeySource"/>, pressed by the test) and <c>SendInput</c> and the clipboard
/// (<see cref="FakeKeystrokeSender"/> and <see cref="FakeClipboard"/>, which record instead of
/// typing into whatever window has focus on this PC).
/// </para>
/// </remarks>
internal sealed class Pipeline : IAsyncDisposable
{
    /// <summary>The saved prompt's trigger.</summary>
    public const string PromptTrigger = "review prompt";

    /// <summary>The saved prompt's body: several lines, a list, and words that are snippet- and list-like.</summary>
    public const string PromptBody =
        "Review this pull request as a senior engineer.\n"
        + "Focus on:\n"
        + "1. Correctness and edge cases\n"
        + "2. Security: secrets, input validation\n"
        + "\n"
        + "Reply with a numbered list. Press enter is not a command here.";

    /// <summary>Dictionary terms, which the cloud request carries in its prompt.</summary>
    public static readonly string[] DictionaryTerms = ["Anthropic", "Parakeet", "Murmur"];

    /// <summary>The deployment names the Accurate and Fast profiles use.</summary>
    public const string AccurateDeployment = "e2e-gpt-4o-transcribe";

    /// <inheritdoc cref="AccurateDeployment"/>
    public const string FastDeployment = "e2e-gpt-4o-mini-transcribe";

    private readonly HttpClient _http;
    private readonly FakeHotkeySource _hotkey = new();
    private readonly WavFileAudioCapture _capture = new();
    private readonly KeyboardLog _log = new();
    private readonly FakeKeystrokeSender _keys;
    private readonly FakeClipboard _clipboard;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _warmUps = [];
    private DictationResult? _result;
    private Exception? _fault;

    private Pipeline(string folder, Func<SettingsData, SettingsData> configure, string? key)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);

        Settings = new AppSettings(Path.Combine(folder, "settings.json"));
        Settings.Update(configure(Settings.Data with { SettingsVersion = SettingsData.CurrentVersion }));
        Settings.Flush();

        File.WriteAllText(
            Path.Combine(folder, "dictionary.txt"),
            string.Join(Environment.NewLine, DictionaryTerms) + Environment.NewLine);
        Dictionary = new DictionaryFile(Path.Combine(folder, "dictionary.txt"));

        Snippets = new SnippetStore(Path.Combine(folder, "snippets.jsonl"));
        Snippets.Add(new SnippetEntry { Trigger = PromptTrigger, Text = PromptBody, Match = SnippetMatch.WholeUtterance });

        Secrets = new ProtectedSecretFile(Path.Combine(folder, "secrets"), new DpapiDataProtector());
        Key = new CloudKey(Secrets);
        if (key is not null) Key.Save(key);

        // From here on, exactly Composition.Create.
        Models = new ModelLocator(() => Settings.Data.ModelDirectory);
        _keys = new FakeKeystrokeSender(_log);
        _clipboard = new FakeClipboard(_log) { Text = "what the user had copied" };
        var injector = new SmartTextInjector(_keys, _clipboard);

        _http = AzureHttp.CreateClient();
        var azureClient = new AzureTranscriptionClient(_http);

        Offline = new LazyTranscriber(
            () =>
            {
                if (Models.Directory is not { } directory) return null;
                Interlocked.Increment(ref _parakeetBuilds);
                return new ParakeetTranscriber(directory, new ParakeetOptions { Threads = Settings.Data.ParakeetThreads });
            },
            TranscriberKind.Parakeet,
            keepWarm: () => EngineStatus.KeepsOfflineLoaded(Settings.Data.Engine, Settings.Data.KeepOfflineEngineWarm),
            unloadAfter: () => TimeSpan.FromMinutes(Settings.Data.UnloadOfflineAfterMinutes is > 0 and var minutes
                    ? minutes
                    : SettingsData.DefaultUnloadOfflineAfterMinutes));

        var keyUse = new DictationKeyUse(Key);
        var azure = new AzureOpenAITranscriber(azureClient, () => AzureProfiles.Resolve(Settings.Data), keyUse.Read);

        Router = new RoutingTranscriber(
            () => Settings.Data.Engine,
            Offline,
            azure,
            cloudTimeout: () => AzureProfiles.RequestTimeout(Settings.Data));

        Engine = new DictationEngine(
            _capture, _hotkey, Router, injector,
            () => Dictionary.Entries,
            clock: null,
            options: () => Settings.Data.ToOptions() with { Snippets = Snippets.Entries },
            injectText: () => Settings.Data.InjectText,
            limits: Composition.LimitsFor(Settings));

        Composition.TrackKeyRejection(Engine, Key, keyUse);

        Engine.Completed += (_, result) =>
        {
            lock (_lock) _result = result;
        };
        Engine.TranscriptionFaulted += (_, ex) =>
        {
            lock (_lock) _fault = ex;
        };

        // As the app does when the key goes down (Composition.WarmUp): load Parakeet, off this
        // thread, if this dictation will use it — so the load overlaps the user speaking. Under a
        // lifetime token, and kept, so DisposeAsync can stop it and wait before the model goes,
        // and so a failed load fails the test instead of going unobserved.
        Engine.StateChanged += (_, state) =>
        {
            if (state != DictationState.Recording) return;
            var token = _lifetime.Token;
            var warmUp = Task.Run(() => Router.WarmUpAsync(token), CancellationToken.None);
            lock (_lock) _warmUps.Add(warmUp);
        };

        Engine.Start();
    }

    private int _parakeetBuilds;

    /// <summary>This pipeline's own folder: settings, dictionary, snippets and the encrypted key.</summary>
    public string Folder { get; }

    /// <summary>The settings, changed by a test between dictations as Settings would.</summary>
    public AppSettings Settings { get; }

    /// <summary>The dictionary.</summary>
    public DictionaryFile Dictionary { get; }

    /// <summary>The saved prompts.</summary>
    public SnippetStore Snippets { get; }

    /// <summary>Where the key is kept, DPAPI-encrypted.</summary>
    public ProtectedSecretFile Secrets { get; }

    /// <summary>The user's key.</summary>
    public CloudKey Key { get; }

    /// <summary>Where the model is.</summary>
    public ModelLocator Models { get; }

    /// <summary>Parakeet, loaded on demand.</summary>
    public LazyTranscriber Offline { get; }

    /// <summary>The engine switch.</summary>
    public RoutingTranscriber Router { get; }

    /// <summary>The dictation engine.</summary>
    public DictationEngine Engine { get; }

    /// <summary>How many times a Parakeet model has been built.</summary>
    public int ParakeetBuilds => Volatile.Read(ref _parakeetBuilds);

    /// <summary>Builds a pipeline in a fresh scratch folder.</summary>
    /// <param name="configure">Changes to the default settings.</param>
    /// <param name="key">An Azure key to save, or null for none.</param>
    /// <returns>A started pipeline.</returns>
    public static Pipeline Create(Func<SettingsData, SettingsData> configure, string? key = null) =>
        new(Path.Combine(E2E.Scratch, $"pipeline-{Guid.NewGuid():N}"), configure, key);

    /// <summary>Settings pointing the cloud modes at <paramref name="mock"/>.</summary>
    /// <param name="data">The settings to change.</param>
    /// <param name="mock">The stand-in endpoint.</param>
    /// <param name="engine">The engine mode.</param>
    /// <returns>The changed settings.</returns>
    public static SettingsData PointedAt(SettingsData data, MockAzureServer mock, EngineMode engine) => data with
    {
        Engine = engine,
        AzureEndpoint = mock.Endpoint.AbsoluteUri,
        AzureApiVersion = AzureTranscriptionOptions.DefaultApiVersion,
        AzureDeploymentAccurate = AccurateDeployment,
        AzureDeploymentFast = FastDeployment,
        AzureActiveProfile = CloudProfile.Accurate,
    };

    /// <summary>Holds the key, plays <paramref name="clip"/> into the microphone, lets go, and waits for the result.</summary>
    /// <param name="clip">What to say.</param>
    /// <param name="timeout">How long the whole dictation may take; two minutes by default.</param>
    /// <returns>What happened.</returns>
    public async Task<Dictation> DictateAsync(Clip clip, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromMinutes(2);
        var token = TestContext.Current.CancellationToken;

        lock (_lock)
        {
            _result = null;
            _fault = null;
        }

        _log.Entries.Clear();
        _keys.Typed.Clear();
        _capture.Load(clip.Samples);

        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transcribing = false;
        void OnState(object? sender, DictationState state)
        {
            if (state == DictationState.Transcribing) transcribing = true;
            else if (state == DictationState.Idle && transcribing) idle.TrySetResult();
        }

        Engine.StateChanged += OnState;
        try
        {
            _hotkey.Press();
            await _capture.Drained.WaitAsync(clip.Duration + TimeSpan.FromSeconds(30), token).ConfigureAwait(false);

            var watch = Stopwatch.StartNew();
            _hotkey.Release();
            await idle.Task.WaitAsync(limit, token).ConfigureAwait(false);
            var wall = watch.Elapsed;

            var keyboard = _log.Entries.ToList();
            string? pasted = null;
            var paste = keyboard.IndexOf("paste");
            if (paste >= 0)
            {
                pasted = keyboard.Take(paste).LastOrDefault(e => e.StartsWith("set:", StringComparison.Ordinal))?["set:".Length..];
            }

            lock (_lock)
            {
                return new Dictation(clip, _result, _fault, keyboard, string.Concat(_keys.Typed), pasted, wall);
            }
        }
        finally
        {
            Engine.StateChanged -= OnState;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // A warm-up still loading must finish or stop before the model it loads is disposed.
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task[] warmUps;
        lock (_lock) warmUps = [.. _warmUps];

        var failures = new List<Exception>();
        foreach (var warmUp in warmUps)
        {
            try
            {
                await warmUp.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped by the cancel above.
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        // Disposes the router, and Parakeet with it.
        await Engine.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
        Settings.Dispose();
        _lifetime.Dispose();

        if (failures.Count > 0) throw new AggregateException("Loading the speech model on key-down failed.", failures);
    }
}
