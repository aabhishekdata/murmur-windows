using System.Net.NetworkInformation;
using Murmur.Abstractions;
using Murmur.Cloud;
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
        ModelLocator models,
        DictationEngine? engine,
        RoutingTranscriber? transcriber,
        IAudioDeviceCatalog? microphones,
        bool platformAvailable,
        CancellationTokenSource lifetime,
        NetworkAvailabilityChangedEventHandler? networkHandler,
        CloudServices cloud,
        HttpClient http,
        SnippetHotkeyBinder? promptHotkeys,
        IGlobalHotkeys? globalHotkeys,
        Extras extras)
    {
        Gesture = extras.Gesture;
        PasteLast = extras.PasteLast;
        Audio = extras.Audio;
        Retry = extras.Retry;
        PromptHotkeys = promptHotkeys;
        _globalHotkeys = globalHotkeys;
        Transcriber = transcriber;
        _lifetime = lifetime;
        _networkHandler = networkHandler;
        _http = http;
        Settings = settings;
        Models = models;
        Dictionary = dictionary;
        Snippets = snippets;
        Transcripts = transcripts;
        Engine = engine;
        Microphones = microphones;
        IsPlatformAvailable = platformAvailable;
        Cloud = cloud;

        // One event for "the answer to Readiness may have changed", whichever piece moved, so the
        // bar has one thing to listen to instead of three.
        settings.Changed += OnReadinessInputChanged;
        models.Changed += OnReadinessInputChanged;
        cloud.Key.Changed += OnReadinessInputChanged;
    }

    /// <summary>Cancelled on dispose; every load and warm-up started here observes it.</summary>
    private readonly CancellationTokenSource _lifetime;

    /// <summary>The fallback's subscription to network changes, removed on dispose.</summary>
    private readonly NetworkAvailabilityChangedEventHandler? _networkHandler;

    /// <summary>The one connection pool to Azure, disposed with the app.</summary>
    private readonly HttpClient _http;

    /// <summary>The registration thread behind <see cref="PromptHotkeys"/>, disposed with the app.</summary>
    private readonly IGlobalHotkeys? _globalHotkeys;

    /// <summary>
    /// Keeps each saved prompt's key combination registered, or null with no platform layer.
    /// </summary>
    public SnippetHotkeyBinder? PromptHotkeys { get; }

    /// <summary>The key and the connection test, for the Settings screen.</summary>
    public CloudServices Cloud { get; }

    /// <summary>Hold, tap and double tap on the push-to-talk key, or null with no platform layer.</summary>
    /// <remarks>The bar listens to it for the hands-free state.</remarks>
    public PushToTalkGesture? Gesture { get; }

    /// <summary>The last transcript and the shortcut that pastes it again.</summary>
    public PasteLastTranscript PasteLast { get; }

    /// <summary>The audio of the last few dictations, for History's retry: in memory, on disk for 24 h by opt-in.</summary>
    public RecentAudio Audio { get; }

    /// <summary>History's "Retry with the other engine", or null with no engines.</summary>
    public TranscriptRetry? Retry { get; }

    /// <summary>The pieces added for hands-free, paste-last and retry, gathered so the constructor stays readable.</summary>
    private sealed record Extras(
        PushToTalkGesture? Gesture,
        PasteLastTranscript PasteLast,
        RecentAudio Audio,
        TranscriptRetry? Retry);

    /// <summary>Whether the chosen engine can transcribe right now, as far as setup goes.</summary>
    /// <remarks>
    /// Cheap: string checks and an in-memory key. The bar reads it on every state change, and on
    /// every keystroke in Settings. The key is not looked at in Parakeet mode, so a user who never
    /// chose the cloud never has the key file read at all.
    /// </remarks>
    public EngineReadiness Readiness
    {
        get
        {
            var data = Settings.Data;
            if (!EngineStatus.UsesCloud(data.Engine))
            {
                return EngineStatus.Evaluate(data.Engine, Models.IsInstalled, cloudConfigured: false, hasApiKey: false);
            }

            return EngineStatus.Evaluate(
                data.Engine,
                Models.IsInstalled,
                AzureProfiles.IsConfigured(data),
                Cloud.Key.HasKey,
                Cloud.Key.State == CloudKeyState.Rejected);
        }
    }

    /// <summary>
    /// Raised when something <see cref="Readiness"/> depends on changes: settings, the model on
    /// disk, or the key. May fire on any thread.
    /// </summary>
    public event EventHandler? ReadinessChanged;

    private void OnReadinessInputChanged(object? sender, EventArgs e) => ReadinessChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>User preferences.</summary>
    public AppSettings Settings { get; }

    /// <summary>
    /// The engine-switching transcriber behind <see cref="Engine"/>, or null with no platform layer.
    /// </summary>
    public RoutingTranscriber? Transcriber { get; }

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

    /// <summary>Where the speech model is, cached. Ask it to refresh rather than polling disk.</summary>
    public ModelLocator Models { get; }

    /// <summary>Whether the speech model was present on disk at the last check.</summary>
    public bool IsModelInstalled => Models.IsInstalled;

    /// <summary>
    /// Builds a graph over storage supplied by the caller, with no engine behind it.
    /// </summary>
    /// <param name="settings">Preferences, usually pointed at a scratch directory.</param>
    /// <param name="dictionary">The dictionary file.</param>
    /// <param name="transcripts">Transcript history.</param>
    /// <param name="snippets">Snippets, or null for an empty store beside the settings file.</param>
    /// <param name="microphones">A stand-in microphone list, or null for none.</param>
    /// <param name="secrets">Where the key is kept, or null for memory only.</param>
    /// <param name="cloudHandler">
    /// Answers the connection test, or null for a handler that refuses every request. A preview
    /// never reaches a network.
    /// </param>
    /// <param name="hotkeys">
    /// Stand-in registrations for prompt shortcuts, or null for none. A fired prompt is not
    /// typed anywhere.
    /// </param>
    /// <param name="injector">
    /// Where the paste-last shortcut and History's Paste deliver, or null for nowhere.
    /// </param>
    /// <param name="engines">
    /// Stand-in engines for History's retry, or null for none — rows then offer no retry.
    /// </param>
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
        IAudioDeviceCatalog? microphones = null,
        ISecretStore? secrets = null,
        HttpMessageHandler? cloudHandler = null,
        IGlobalHotkeys? hotkeys = null,
        ITextInjector? injector = null,
        Func<TranscriberKind, ITranscriber?>? engines = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(transcripts);

        snippets ??= new SnippetStore(Path.Combine(
            Path.GetDirectoryName(dictionary.FilePath) ?? Path.GetTempPath(), "snippets.jsonl"));
        var promptHotkeys = hotkeys is null ? null : new SnippetHotkeyBinder(snippets, hotkeys, new NoTextInjector());

        var http = AzureHttp.CreateClient(cloudHandler ?? new OfflineHandler());
        var key = new CloudKey(secrets ?? new SessionSecretStore());
        var cloud = new CloudServices(settings, key, new ConnectionTester(new AzureTranscriptionClient(http)));

        var pasteLast = new PasteLastTranscript(
            hotkeys, injector ?? new NoTextInjector(), heldByPrompt: chord => HeldByPrompt(snippets, chord));
        if (promptHotkeys is not null) promptHotkeys.PasteLastChord = () => pasteLast.Chord;
        pasteLast.Remember(transcripts.Latest?.Text);
        if (hotkeys is not null) KeepBound(settings, pasteLast);
        var audio = new RecentAudio();
        var retry = engines is null ? null : new TranscriptRetry(
            audio,
            engines,
            () => dictionary.Entries,
            () => settings.Data.ToOptions() with { Snippets = snippets.Entries },
            () => AzureProfiles.EffectiveProfile(settings.Data));

        return new(settings,
            dictionary,
            snippets,
            transcripts,
            new ModelLocator(() => settings.Data.ModelDirectory),
            engine: null,
            transcriber: null,
            microphones,
            platformAvailable: false,
            new CancellationTokenSource(),
            networkHandler: null,
            cloud,
            http,
            promptHotkeys,
            globalHotkeys: null,
            new Extras(Gesture: null, pasteLast, audio, retry));
    }

    /// <summary>Builds the object graph.</summary>
    public static Composition Create()
    {
        var settings = new AppSettings(AppSettings.DefaultPath);
        var dictionary = new DictionaryFile(DictionaryFile.DefaultPath);
        var snippets = new SnippetStore(SnippetStore.DefaultPath);
        var transcripts = new TranscriptStore(TranscriptStore.DefaultPath);
        var models = new ModelLocator(() => settings.Data.ModelDirectory);

        // The file is written on a timer thread now, so a failure there has no caller to
        // catch it. Logged rather than lost; the values in memory are unaffected.
        settings.SaveFailed += (_, ex) => CrashLog.Write("Saving settings failed", ex);

        // The device ID is read through a closure rather than passed by value, so choosing a
        // different microphone in Settings or on the bar takes effect on the next dictation
        // instead of at the next launch.
        var capture = PlatformFactory.CreateAudioCapture(() => settings.Data.MicrophoneId);
        // Hold works as it always has; a tap followed by a second press latches hands-free. The
        // setting is read per tap, so switching it off applies to the very next one.
        var rawHotkey = PlatformFactory.CreateHotkeySource(settings.Data.PushToTalkKey);
        var hotkey = rawHotkey is null
            ? null
            : new PushToTalkGesture(rawHotkey, () => settings.Data.HandsFreeDoubleTap);
        var microphones = PlatformFactory.CreateAudioDeviceCatalog();

        // SendInput does the keystrokes; whether to type or paste, and putting the clipboard
        // back afterwards, is decided in Core.
        var keystrokes = PlatformFactory.CreateKeystrokeSender();
        ITextInjector? injector = keystrokes is null
            ? null
            : new SmartTextInjector(keystrokes, new AvaloniaClipboardService());

        // The key is DPAPI-encrypted for this Windows user in a file of its own, never in
        // settings.json. Without DPAPI — off Windows — it is held for the session only rather
        // than written anywhere in the clear.
        var protector = PlatformFactory.CreateDataProtector();
        ISecretStore secrets = protector is not null
            ? new ProtectedSecretFile(ProtectedSecretFile.DefaultDirectory, protector)
            : new SessionSecretStore();
        var cloudKey = new CloudKey(secrets);

        // One client for the life of the process, shared by dictation and the Settings test, so
        // the TLS connection to Azure is reused. Nothing is sent unless a cloud engine is chosen.
        var http = AzureHttp.CreateClient();
        var azureClient = new AzureTranscriptionClient(http);
        var cloud = new CloudServices(settings, cloudKey, new ConnectionTester(azureClient));

        DictationEngine? engine = null;
        RoutingTranscriber? router = null;
        SnippetHotkeyBinder? promptHotkeys = null;
        IGlobalHotkeys? globalHotkeys = null;
        NetworkAvailabilityChangedEventHandler? networkHandler = null;
        var lifetime = new CancellationTokenSource();
        var available = capture is not null && hotkey is not null && injector is not null;

        // In memory, and on disk for a day only while the user has opted in. Anything past its
        // day — or everything, with the setting off — is deleted here, before it could be read.
        var audio = new RecentAudio(RecentAudio.DefaultDirectory, () => settings.Data.KeepRecentAudioOnDisk);
        audio.DiskFailed += (_, ex) => CrashLog.Write("Keeping recent audio on disk failed", ex);
        audio.Purge();
        var keptOnDisk = settings.Data.KeepRecentAudioOnDisk;
        settings.Changed += (_, _) =>
        {
            // Settings change often (the bar's position, every keystroke); only the switch matters.
            var keep = settings.Data.KeepRecentAudioOnDisk;
            if (keep == keptOnDisk) return;
            keptOnDisk = keep;
            if (!keep) audio.ForgetOnDisk();
        };

        PasteLastTranscript? pasteLast = null;
        TranscriptRetry? retry = null;

        if (available)
        {
            // Parakeet is built on demand from whatever the locator last found, so a model
            // installed after launch is picked up once the locator is refreshed — it used to
            // need a restart — and the thread count applies at the next load.
            var offline = new LazyTranscriber(
                () => models.Directory is { } directory
                    ? new ParakeetTranscriber(
                        directory, new ParakeetOptions { Threads = settings.Data.ParakeetThreads })
                    : null,
                TranscriberKind.Parakeet,
                keepWarm: () => EngineStatus.KeepsOfflineLoaded(
                    settings.Data.Engine, settings.Data.KeepOfflineEngineWarm),
                unloadAfter: () => TimeSpan.FromMinutes(settings.Data.UnloadOfflineAfterMinutes is > 0 and var minutes
                    ? minutes
                    : SettingsData.DefaultUnloadOfflineAfterMinutes));

            // The deployment and the key are read per request, so the Accurate/Fast switch and a
            // replaced key apply to the next dictation.
            var keyUse = new DictationKeyUse(cloudKey);
            var azure = new AzureOpenAITranscriber(
                azureClient,
                () => AzureProfiles.Resolve(settings.Data),
                keyUse.Read);

            // In fallback mode Parakeet is asked once the cloud has had its time. The client
            // enforces the same budget itself; this is the backstop should it ever not.
            router = new RoutingTranscriber(
                () => settings.Data.Engine,
                offline,
                azure,
                cloudTimeout: () => AzureProfiles.RequestTimeout(settings.Data),
                // Without the model there is nothing to fall back to: the cloud is always asked,
                // and its own error is the one shown.
                offlineAvailable: () => models.IsInstalled);

            engine = new DictationEngine(
                capture!, hotkey!, router, injector!,
                () => dictionary.Entries,
                clock: null,
                // Read fresh per utterance, for the same reason the dictionary is: a toggle
                // flipped in Settings applies to the very next thing the user says.
                options: () => settings.Data.ToOptions() with { Snippets = snippets.Entries },
                // The "Type transcripts into the focused app" toggle. It was saved and shown
                // but never consulted, so switching it off changed nothing.
                injectText: () => settings.Data.InjectText,
                limits: LimitsFor(settings));

            TrackKeyRejection(engine, cloudKey, keyUse);

            // A prompt's key combination pastes it without speaking. Pressed mid-recording, it
            // abandons what was being said: the user asked for the prompt instead.
            globalHotkeys = PlatformFactory.CreateGlobalHotkeys();
            if (globalHotkeys is not null)
            {
                promptHotkeys = new SnippetHotkeyBinder(snippets, globalHotkeys, injector!, engine.CancelAsync);
                promptHotkeys.FireFaulted += (_, ex) => CrashLog.Write("Pasting a prompt from its shortcut failed", ex);
            }

            // After the prompts, so a prompt the user gave the same combination keeps it.
            pasteLast = new PasteLastTranscript(
                globalHotkeys, injector!, engine.CancelAsync, heldByPrompt: chord => HeldByPrompt(snippets, chord));
            var pasteLastChord = pasteLast;
            if (promptHotkeys is not null) promptHotkeys.PasteLastChord = () => pasteLastChord.Chord;
            pasteLast.PasteFaulted += (_, ex) => CrashLog.Write("Pasting the last transcript failed", ex);
            pasteLast.Remember(transcripts.Latest?.Text);
            KeepBound(settings, pasteLast);

            // The other engine, for History: Parakeet when it is installed, Azure only while an
            // Azure mode is chosen and set up. A retry obeys the same privacy contract a
            // dictation does.
            var routing = router;
            retry = new TranscriptRetry(
                audio,
                kind => TranscriptRetry.MayUse(
                    kind,
                    settings.Data.Engine,
                    models.IsInstalled,
                    AzureProfiles.IsConfigured(settings.Data) && cloudKey.HasKey && cloudKey.State != CloudKeyState.Rejected)
                    ? routing.For(kind)
                    : null,
                () => dictionary.Entries,
                () => settings.Data.ToOptions() with { Snippets = snippets.Entries },
                () => AzureProfiles.EffectiveProfile(settings.Data),
                LimitsFor(settings));

            // The recogniser must be loaded before it can transcribe; forgetting this call
            // was a shipped bug — the meter moved but no text ever appeared. LoadAsync is
            // synchronous work behind an async signature (constructing OfflineRecognizer reads
            // ~660 MB of ONNX weights), so it is pushed off the startup thread; a failure is
            // logged rather than lost. Parakeet mode loads here, exactly as it always has; the
            // cloud modes leave it unloaded until a dictation might need it.
            if (EngineStatus.LoadsOfflineAtStartup(settings.Data.Engine)) WarmUp(router, lifetime.Token);

            // When the key goes down, so the load overlaps the user speaking. Does nothing in
            // cloud-only mode, nor in fallback mode unless the cloud is being skipped, and next
            // to nothing when the model is already loaded.
            var warming = router;
            engine.StateChanged += (_, state) =>
            {
                if (state == DictationState.Recording) WarmUp(warming, lifetime.Token);
            };

            // Lets the fallback skip the cloud the moment the network goes, instead of waiting
            // out a connection timeout on every dictation until the breaker trips.
            var fallback = router.Fallback;
            networkHandler = (_, e) => fallback.OnNetworkAvailabilityChanged(e.IsAvailable);
            try
            {
                NetworkChange.NetworkAvailabilityChanged += networkHandler;
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                networkHandler = null;
                CrashLog.Write("Watching for network changes failed", ex);
            }

            // Completed fires on the engine's background processing thread (the chain starts
            // on the dedicated keyboard-hook thread and stays off the UI thread throughout).
            // TranscriptStore.Add raises Changed synchronously, and TranscriptionsView
            // rebuilds live controls from that event — which Avalonia only allows on the UI
            // thread. Calling Add directly from here was a shipped bug: the cross-thread
            // exception propagated back into the engine, was swallowed, and blocked text
            // injection. Post rather than Invoke, so the completion thread never blocks
            // waiting on the UI thread (which may be busy, or gone during shutdown).
            var remembered = pasteLast;
            engine.Completed += (_, result) =>
            {
                // Held for the paste-last shortcut whether or not history is kept.
                remembered.Remember(result.Text);

                if (!settings.Data.KeepHistory) return;

                // Only alongside a history row that could offer the retry; on disk too when opted in.
                audio.Add(result.Id, result.Audio);

                var record = new TranscriptRecord
                {
                    Id = result.Id,
                    Engine = result.Engine,
                    FellBack = result.FellBack,
                    Profile = result.Engine == TranscriberKind.Azure ? AzureProfiles.EffectiveProfile(settings.Data) : null,
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

        ApplyLive(settings, models, engine, router, lifetime.Token);

        // Without a platform layer nothing can be pasted, but the last transcript is still held
        // for the tray's and the bar's Copy.
        pasteLast ??= new PasteLastTranscript(hotkeys: null, new NoTextInjector());
        if (pasteLast.Text is null) pasteLast.Remember(transcripts.Latest?.Text);

        return new Composition(
            settings, dictionary, snippets, transcripts, models, engine, router, microphones, available,
            lifetime, networkHandler, cloud, http, promptHotkeys, globalHotkeys,
            new Extras(hotkey, pasteLast, audio, retry));
    }

    /// <summary>Whether an enabled saved prompt has <paramref name="chord"/>.</summary>
    private static bool HeldByPrompt(SnippetStore snippets, HotkeyChord chord) =>
        snippets.Entries.Any(e => e.IsEnabled && e.Hotkey == chord);

    /// <summary>Registers the paste-last shortcut now, and again whenever Settings changes it.</summary>
    /// <param name="settings">Where the shortcut is kept.</param>
    /// <param name="pasteLast">The service to bind.</param>
    /// <remarks>
    /// Synchronous on the thread that changed the setting, so Settings can read the new status the
    /// moment its own update returns.
    /// </remarks>
    internal static void KeepBound(AppSettings settings, PasteLastTranscript pasteLast)
    {
        var chord = settings.Data.PasteLastHotkey;
        pasteLast.Bind(chord);

        settings.Changed += (_, _) =>
        {
            if (settings.Data.PasteLastHotkey == chord) return;
            chord = settings.Data.PasteLastHotkey;
            pasteLast.Bind(chord);
        };
    }

    /// <summary>Keeps the key's rejected state in step with what dictation finds.</summary>
    /// <param name="engine">The dictation engine.</param>
    /// <param name="key">The user's key.</param>
    /// <param name="use">Which key the dictation sent, so the outcome is held against that one.</param>
    /// <remarks>
    /// A 401 is the key, not the network, so the bar says "Key rejected" until a key is
    /// saved again or a cloud dictation gets through. Both events fire on the processing
    /// thread; the bar marshals the change they cause. A 401 for a key the user has replaced
    /// while the request was out says nothing about the new one, and is ignored.
    /// </remarks>
    internal static void TrackKeyRejection(DictationEngine engine, CloudKey key, DictationKeyUse use)
    {
        engine.TranscriptionFaulted += (_, ex) =>
        {
            if (ex is TranscriptionException failure && CloudServices.IsKeyRejection(failure.Failure)
                && use.LastGeneration is { } generation)
            {
                key.MarkRejected(generation);
            }
        };

        engine.Completed += (_, result) =>
        {
            if (result.Engine == TranscriberKind.Azure && !result.FellBack && use.LastGeneration is { } generation)
            {
                key.MarkAccepted(generation);
            }
        };
    }

    /// <summary>The engine's time limits, with room for the cloud's budget and then Parakeet's.</summary>
    /// <param name="settings">The settings, read at the start of each utterance.</param>
    /// <returns>Limits whose fixed part, in the cloud modes, outlasts the cloud timeout by the default ten seconds.</returns>
    /// <remarks>
    /// The engine's own budget is ten seconds plus twice the audio, which for a short utterance
    /// is less than the cloud's 30 s. Left alone, a slow cloud would run the whole dictation
    /// out of time before fallback mode ever got to ask Parakeet. Parakeet mode keeps the plain
    /// budget, and a changed cloud timeout applies from the next dictation.
    /// </remarks>
    internal static DictationEngineOptions LimitsFor(AppSettings settings) => new()
    {
        ExtraBaseBudget = () => EngineStatus.UsesCloud(settings.Data.Engine)
            ? AzureProfiles.RequestTimeout(settings.Data)
            : TimeSpan.Zero,
    };

    /// <summary>Loads the offline engine in the background if the next dictation will use it.</summary>
    private static void WarmUp(RoutingTranscriber router, CancellationToken cancellationToken) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await router.WarmUpAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                CrashLog.Write("Speech model load failed", ex);
            }
        }, CancellationToken.None);

    /// <summary>
    /// Applies the settings that take effect without a restart.
    /// </summary>
    /// <remarks>
    /// Nothing used to subscribe to <see cref="AppSettings.Changed"/>, so choosing a new
    /// push-to-talk key in Settings did nothing until the app was relaunched — while the
    /// Settings window showed the new key as chosen. Everything else is already read fresh
    /// on each dictation through a closure; these are the settings that are not. The engine
    /// itself is read per dictation too, but switching to Parakeet loads it at once, so the
    /// first dictation after the switch does not pay for the load.
    /// </remarks>
    internal static void ApplyLive(
        AppSettings settings,
        ModelLocator models,
        DictationEngine? engine,
        RoutingTranscriber? transcriber = null,
        CancellationToken cancellationToken = default)
    {
        var key = settings.Data.PushToTalkKey;
        var modelDirectory = settings.Data.ModelDirectory;
        var mode = settings.Data.Engine;

        settings.Changed += (_, _) =>
        {
            var data = settings.Data;

            if (data.PushToTalkKey != key)
            {
                key = data.PushToTalkKey;
                if (engine is not null) _ = SetKeyAsync(engine, key);
            }

            if (!string.Equals(data.ModelDirectory, modelDirectory, StringComparison.Ordinal))
            {
                modelDirectory = data.ModelDirectory;
                models.Refresh();
            }

            if (data.Engine != mode)
            {
                mode = data.Engine;
                if (transcriber is not null && EngineStatus.LoadsOfflineAtStartup(mode))
                {
                    WarmUp(transcriber, cancellationToken);
                }
            }
        };
    }

    private static async Task SetKeyAsync(DictationEngine engine, int key)
    {
        // Fire-and-forget from a settings event, so nothing above would observe a failure.
        try
        {
            await engine.SetPushToTalkKeyAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Changing the push-to-talk key failed", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_networkHandler is not null) NetworkChange.NetworkAvailabilityChanged -= _networkHandler;

        Settings.Changed -= OnReadinessInputChanged;
        Models.Changed -= OnReadinessInputChanged;
        Cloud.Key.Changed -= OnReadinessInputChanged;

        try
        {
            // Before the engine, so a shortcut pressed while quitting cannot reach it.
            PromptHotkeys?.Dispose();
            PasteLast.Dispose();
            _globalHotkeys?.Dispose();

            if (Engine is not null) await Engine.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
            Audio.Dispose();

            // After the engine, so no dictation still in flight loses its connection mid-request.
            _http.Dispose();

            // Writes are debounced, so the last change — very often the bar's final position —
            // may still be waiting on the timer when the user quits. In a finally, so an engine
            // that fails to shut down cleanly does not also lose it.
            Settings.Dispose();
        }
    }
}

/// <summary>Reads the key for dictation and remembers which one it handed out.</summary>
/// <param name="key">The user's key.</param>
/// <remarks>
/// The engine reports a 401 after the fact, with no way to say which key was sent; this does.
/// Dictations run one at a time, so the last key read is the one the outcome belongs to.
/// </remarks>
internal sealed class DictationKeyUse(CloudKey key)
{
    private long _last = -1;

    /// <summary>The generation of the key last handed to a request, or null before the first.</summary>
    public long? LastGeneration => Interlocked.Read(ref _last) is var last and >= 0 ? last : null;

    /// <summary>Reads the key and records its generation.</summary>
    /// <returns>The key, or null when none is saved.</returns>
    public string? Read()
    {
        var value = key.Read(out var generation);
        Interlocked.Exchange(ref _last, generation);
        return value;
    }
}

/// <summary>An injector that delivers nothing, for a preview's prompt shortcuts.</summary>
internal sealed class NoTextInjector : ITextInjector
{
    /// <inheritdoc />
    public ValueTask<bool> InjectAsync(string text, CancellationToken cancellationToken) => ValueTask.FromResult(false);

    /// <inheritdoc />
    public ValueTask<bool> PressEnterAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
}
