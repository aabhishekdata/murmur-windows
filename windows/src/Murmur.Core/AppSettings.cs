using System.Text.Json;
using System.Text.Json.Serialization;
using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>User preferences.</summary>
public sealed record SettingsData
{
    /// <summary>The settings generation this build writes. See <see cref="SettingsVersion"/>.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Which generation of first-run setup this user has been through; 0 means none yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gates the onboarding window (<see cref="Onboarding"/>). A fresh install has no settings file,
    /// starts at 0, and is walked through setup; finishing it writes <see cref="CurrentVersion"/>.
    /// </para>
    /// <para>
    /// <b>A settings file written before this field existed is treated as already onboarded</b> —
    /// <see cref="AppSettings"/> raises it to <see cref="CurrentVersion"/> on load — so everyone
    /// already using Murmur is never shown a welcome screen. That is decided by whether the field
    /// is <i>present</i> in the file, not by its value: a new user who quits half-way through setup
    /// has a file with an explicit 0, and sees setup again next time.
    /// </para>
    /// </remarks>
    public int SettingsVersion { get; init; }

    /// <summary>
    /// Virtual-key code of the push-to-talk key. Defaults to Right Ctrl (0xA3).
    /// </summary>
    /// <remarks>
    /// <b>Not Right Alt.</b> On German, Polish, UK, Nordic and most Latin-American layouts
    /// Right Alt is AltGr — it is how those users type <c>@</c>, <c>€</c>, <c>\</c> and
    /// <c>|</c>. Right Ctrl produces no character on any layout.
    /// </remarks>
    public int PushToTalkKey { get; init; } = 0xA3;

    /// <summary>Where the speech model lives, or null to search the default locations.</summary>
    public string? ModelDirectory { get; init; }

    /// <summary>Whether to type the transcript into the focused app.</summary>
    public bool InjectText { get; init; } = true;

    /// <summary>Whether to keep a transcript history.</summary>
    public bool KeepHistory { get; init; } = true;

    /// <summary>Which screen edge the floating bar is docked to.</summary>
    public ScreenEdge FlowBarEdge { get; init; } = ScreenEdge.Bottom;

    /// <summary>
    /// How far along that edge the bar sits, 0…1.
    /// </summary>
    /// <remarks>
    /// A fraction rather than a pixel position, on purpose. An absolute coordinate saved at a
    /// two-monitor desk puts the bar off-screen the next time the laptop is undocked — and a
    /// frameless, always-on-top window that keeps itself out of the taskbar cannot be dragged
    /// back from there. See <see cref="FlowBarPlacement"/>.
    /// </remarks>
    public double FlowBarOffset { get; init; } = FlowBarPlacement.DefaultOffset;

    /// <summary>
    /// The microphone to record from, or null to follow the system default.
    /// </summary>
    /// <remarks>
    /// An explicit choice is honoured by ID and outranks both of Windows' defaults. A device
    /// that has since been unplugged falls back rather than failing the recording.
    /// </remarks>
    public string? MicrophoneId { get; init; }

    /// <summary>Whether to drop "um", "uh" and the rest from transcribed text.</summary>
    public bool RemoveFillers { get; init; } = true;

    /// <summary>Whether to honour spoken retractions like "wait no".</summary>
    public bool ApplySelfCorrections { get; init; } = true;

    /// <summary>Whether a trailing "press enter" submits instead of being typed.</summary>
    public bool EnterCommand { get; init; } = true;

    /// <summary>Whether spoken snippet triggers expand.</summary>
    public bool ExpandSnippets { get; init; } = true;

    /// <summary>Whether spoken enumerations become real lists.</summary>
    public bool FormatLists { get; init; } = true;

    /// <summary>
    /// Dictionary suggestions the user has turned down.
    /// </summary>
    /// <remarks>
    /// Keyed by <c>heard=&gt;intended</c>, lowercased. Without this the same rejected suggestion
    /// reappears every time the Hub is opened, which turns a helpful prompt into nagging.
    /// </remarks>
    public string[] DismissedSuggestions { get; init; } = [];

    /// <summary>Whether the floating bar is shown.</summary>
    /// <remarks>
    /// Hiding it leaves the hotkey and the tray icon working. The bar is an indicator and a
    /// click target; it is never the thing that makes dictation possible.
    /// </remarks>
    public bool ShowFlowBar { get; init; } = true;

    /// <summary>Which speech engine dictation uses.</summary>
    /// <remarks>
    /// Parakeet, on this machine, unless the user chooses otherwise. Cloud transcription is
    /// opt-in and always will be.
    /// </remarks>
    public EngineMode Engine { get; init; } = EngineMode.Parakeet;

    /// <summary>
    /// Whether Parakeet stays loaded while it is only the fallback.
    /// </summary>
    /// <remarks>
    /// Off by default: holding ~660 MB for a fallback that may never be needed is what a
    /// cloud engine is chosen to avoid. In Parakeet mode the model always stays loaded and this
    /// is not consulted.
    /// </remarks>
    public bool KeepOfflineEngineWarm { get; init; }

    /// <summary>Minutes Parakeet may sit unused before it is unloaded, when it is only the fallback.</summary>
    /// <remarks>Zero or less is read as <see cref="DefaultUnloadOfflineAfterMinutes"/>.</remarks>
    public int UnloadOfflineAfterMinutes { get; init; } = DefaultUnloadOfflineAfterMinutes;

    /// <summary>The default for <see cref="UnloadOfflineAfterMinutes"/>.</summary>
    public const int DefaultUnloadOfflineAfterMinutes = 10;

    /// <summary>Parakeet inference threads, or 0 to size automatically.</summary>
    /// <remarks>Applies the next time the model loads.</remarks>
    public int ParakeetThreads { get; init; }

    /// <summary>The Azure OpenAI resource, as pasted; null until the user sets up the cloud.</summary>
    /// <remarks>
    /// <b>No key is ever stored in this record.</b> It is written to <c>settings.json</c> in
    /// plain text; the key lives in a DPAPI-encrypted file of its own (see
    /// <see cref="ProtectedSecretFile"/>), and a test serialises every field here to prove it.
    /// </remarks>
    public string? AzureEndpoint { get; init; }

    /// <summary>The Azure OpenAI <c>api-version</c>.</summary>
    public string AzureApiVersion { get; init; } = "2025-03-01-preview";

    /// <summary>The deployment of <c>gpt-4o-transcribe</c>, the Accurate profile.</summary>
    public string? AzureDeploymentAccurate { get; init; }

    /// <summary>The deployment of <c>gpt-4o-mini-transcribe</c>, the Fast profile.</summary>
    public string? AzureDeploymentFast { get; init; }

    /// <summary>Which of the two deployments dictation uses.</summary>
    /// <remarks>When only one deployment is filled in, that one is used whatever this says.</remarks>
    public CloudProfile AzureActiveProfile { get; init; } = CloudProfile.Accurate;

    /// <summary>An ISO-639-1 language code for the cloud engine, or null to detect it.</summary>
    public string? CloudLanguage { get; init; }

    /// <summary>
    /// How long one dictation may spend with the cloud, retries included, before it counts as
    /// failed — and, in fallback mode, before Parakeet is asked instead.
    /// </summary>
    public int CloudRequestTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// What language dictation is in. Hindi and Hinglish are understood by the cloud engine only.
    /// </summary>
    /// <remarks>See <see cref="LanguageModes"/> for what each one sends.</remarks>
    public LanguageMode Language { get; init; } = LanguageMode.Auto;

    /// <summary>
    /// Whether the audio History keeps for "Retry with the other engine" is also written to disk,
    /// for 24 hours, so a retry survives a restart. Off by default: audio otherwise stays in memory.
    /// </summary>
    /// <remarks>False is the default, so a file without it needs no help from <c>FillMissing</c>.</remarks>
    public bool KeepRecentAudioOnDisk { get; init; }

    /// <summary>Whether a double tap of the push-to-talk key latches recording on, hands-free.</summary>
    public bool HandsFreeDoubleTap { get; init; } = true;

    /// <summary>The shortcut that pastes the last transcript again, or null for none.</summary>
    /// <remarks>
    /// <para>
    /// None until the user records one. A global shortcut is taken from every application, and
    /// the obvious candidates are already somebody's: Ctrl+Alt+V is Paste Special in Word, Excel,
    /// Outlook and PowerPoint, and Ctrl+Alt is AltGr on many European layouts. Taking one of
    /// those silently, on every install, breaks a shortcut the user relies on with nothing on
    /// screen to say Murmur did it.
    /// </para>
    /// <para>Stored as text, as a prompt's shortcut is.</para>
    /// </remarks>
    [JsonConverter(typeof(HotkeyChordJsonConverter))]
    public HotkeyChord? PasteLastHotkey { get; init; }

    /// <summary>How fast the user types, for Home's "time saved". 40 words a minute is typical.</summary>
    public int TypingWordsPerMinute { get; init; } = UsageStats.DefaultTypingWordsPerMinute;

    /// <summary>US dollars per minute of audio on the Accurate deployment, for Home's cost estimate.</summary>
    /// <remarks>An estimate the user can correct to their own Azure price; nothing is billed through Murmur.</remarks>
    public double CloudPricePerMinuteAccurate { get; init; } = UsageStats.DefaultPricePerMinuteAccurate;

    /// <summary>US dollars per minute of audio on the Fast deployment, for Home's cost estimate.</summary>
    public double CloudPricePerMinuteFast { get; init; } = UsageStats.DefaultPricePerMinuteFast;
}

/// <summary>Which Azure deployment the cloud engine uses.</summary>
public enum CloudProfile
{
    /// <summary><c>gpt-4o-transcribe</c>: the better transcript. The default.</summary>
    Accurate,

    /// <summary><c>gpt-4o-mini-transcribe</c>: quicker and cheaper.</summary>
    Fast,
}

/// <summary>Settings, persisted as JSON.</summary>
/// <remarks>
/// <para>
/// <see cref="Update"/> changes <see cref="Data"/> and raises <see cref="Changed"/> at once,
/// but the file is written later, on a background thread, once updates have stopped for
/// <see cref="SaveDelay"/>. It used to be written synchronously on the UI thread on every
/// call — and dragging the bar calls it on every snap, so a slow disk or an antivirus scan
/// stalled the interface mid-gesture.
/// </para>
/// <para>
/// The write is atomic: the JSON goes to a temporary file beside the real one, which is then
/// moved over it. A crash or power cut mid-write leaves either the old settings or the new
/// ones, never half of a file that <c>Load</c> would then discard as corrupt.
/// </para>
/// <para>
/// Call <see cref="Flush"/> before the process exits, or the last change can be lost.
/// </para>
/// </remarks>
public sealed class AppSettings : IDisposable
{
    /// <summary>How long updates must pause before the file is written.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private readonly string _path;
    private readonly ITimer _timer;
    private readonly Lock _lock = new();
    private readonly Lock _writeLock = new();

    /// <summary>The snapshot waiting to be written, or null when the file is current.</summary>
    private SettingsData? _pending;

    /// <summary>Counts updates, so a flush can tell whether one arrived while it was writing.</summary>
    private long _version;

    /// <summary>Loads settings from <paramref name="path"/>, or defaults if absent.</summary>
    /// <param name="path">The settings file.</param>
    /// <param name="time">Drives the save delay; defaults to the system clock.</param>
    public AppSettings(string path, TimeProvider? time = null)
    {
        _path = path;
        Data = Load(path);

        _timer = (time ?? TimeProvider.System).CreateTimer(
            static state => ((AppSettings)state!).Flush(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>The default location.</summary>
    public static string DefaultPath => MurmurPaths.In("settings.json");

    /// <summary>Current values.</summary>
    public SettingsData Data { get; private set; }

    /// <summary>Whether a change is waiting to be written.</summary>
    public bool HasPendingSave
    {
        get
        {
            lock (_lock) return _pending is not null;
        }
    }

    /// <summary>Raised on the calling thread as soon as <see cref="Update"/> changes the values.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised when writing the file fails. May fire on a background thread.
    /// </summary>
    /// <remarks>
    /// The values in memory are still the new ones, and the next update or flush tries again.
    /// A settings file held open by an editor, a backup agent or a second copy of the app is
    /// an annoyance, not a reason to take the app down.
    /// </remarks>
    public event EventHandler<Exception>? SaveFailed;

    /// <summary>Replaces the settings and schedules them to be written.</summary>
    /// <param name="data">The new values.</param>
    public void Update(SettingsData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        lock (_lock)
        {
            Data = data;
            _pending = data;
            _version++;

            // Restarting the timer is the debounce: a burst of updates writes once, at the end.
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes any pending change now, on the calling thread.</summary>
    /// <remarks>Safe to call when nothing is pending, and from any thread.</remarks>
    public void Flush()
    {
        Exception? failure = null;

        // The write lock serialises this with the timer's own flush, so two writers never race
        // for the temporary file. The state lock is held only to take the snapshot and to clear
        // it afterwards — never across the disk I/O — because Update takes it on the UI thread,
        // and a slow write must not stall a drag of the bar.
        lock (_writeLock)
        {
            SettingsData data;
            long version;

            lock (_lock)
            {
                if (_pending is not { } pending) return;

                data = pending;
                version = _version;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            try
            {
                Write(_path, data);

                // An update that arrived mid-write is newer than what was written, so it stays
                // pending, and its own restarted timer writes it.
                lock (_lock)
                {
                    if (_version == version) _pending = null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
            }
        }

        // Outside both locks: the handler logs, which is more file I/O.
        if (failure is not null) SaveFailed?.Invoke(this, failure);
    }

    /// <summary>Flushes, then stops the save timer.</summary>
    public void Dispose()
    {
        Flush();
        _timer.Dispose();
    }

    private static void Write(string path, SettingsData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, SettingsJsonContext.Default.SettingsData));
        File.Move(temporary, path, overwrite: true);
    }

    private static SettingsData Load(string path)
    {
        // No file at all is a fresh install: defaults, and SettingsVersion 0, which is what
        // shows the onboarding window.
        if (!File.Exists(path)) return new SettingsData();

        // Corrupt or unreadable settings must never stop the app launching — defaults are
        // always a working configuration. Someone had a settings file, though, so they have used
        // Murmur before and are not shown the welcome screen again.
        var existing = new SettingsData { SettingsVersion = SettingsData.CurrentVersion };

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsData);
            if (data is null) return existing;

            data = FillMissing(json, data);

            return Mentions(json, nameof(SettingsData.SettingsVersion))
                ? data
                : data with { SettingsVersion = SettingsData.CurrentVersion };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return existing;
        }
    }

    /// <summary>Gives fields a file does not mention their real defaults rather than zero.</summary>
    /// <remarks>
    /// The source-generated reader sets every init-only property, so one missing from the file
    /// arrives as <c>false</c>, <c>0</c> or null — not as the default written beside it. For the
    /// fields added after files were already in use, where zero is wrong (hands-free off, a
    /// free cloud, Parakeet unloaded a minute after use), the default is put back here. Null is
    /// already the default for the paste-last shortcut, so a file without it needs nothing.
    /// </remarks>
    private static SettingsData FillMissing(string json, SettingsData data)
    {
        var defaults = new SettingsData();

        return data with
        {
            HandsFreeDoubleTap = Mentions(json, nameof(SettingsData.HandsFreeDoubleTap))
                ? data.HandsFreeDoubleTap
                : defaults.HandsFreeDoubleTap,
            UnloadOfflineAfterMinutes = Mentions(json, nameof(SettingsData.UnloadOfflineAfterMinutes))
                ? data.UnloadOfflineAfterMinutes
                : defaults.UnloadOfflineAfterMinutes,
            AzureApiVersion = Mentions(json, nameof(SettingsData.AzureApiVersion))
                ? data.AzureApiVersion
                : defaults.AzureApiVersion,
            CloudRequestTimeoutSeconds = Mentions(json, nameof(SettingsData.CloudRequestTimeoutSeconds))
                ? data.CloudRequestTimeoutSeconds
                : defaults.CloudRequestTimeoutSeconds,
            TypingWordsPerMinute = Mentions(json, nameof(SettingsData.TypingWordsPerMinute))
                ? data.TypingWordsPerMinute
                : defaults.TypingWordsPerMinute,
            CloudPricePerMinuteAccurate = Mentions(json, nameof(SettingsData.CloudPricePerMinuteAccurate))
                ? data.CloudPricePerMinuteAccurate
                : defaults.CloudPricePerMinuteAccurate,
            CloudPricePerMinuteFast = Mentions(json, nameof(SettingsData.CloudPricePerMinuteFast))
                ? data.CloudPricePerMinuteFast
                : defaults.CloudPricePerMinuteFast,
        };
    }

    /// <summary>Whether the JSON object in <paramref name="json"/> has a property called <paramref name="name"/>.</summary>
    private static bool Mentions(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(name, out _);
    }
}

/// <summary>Whether the first-run window should be shown.</summary>
/// <remarks>
/// A rule rather than a flag check scattered through the app, so the app, the onboarding window
/// and the tests agree on it. See <see cref="SettingsData.SettingsVersion"/>.
/// </remarks>
public static class Onboarding
{
    /// <summary>Whether <paramref name="data"/> belongs to someone who has not been through setup.</summary>
    /// <param name="data">The settings as loaded.</param>
    /// <returns>True for a fresh install, or one that quit setup half-way.</returns>
    public static bool IsNeeded(SettingsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.SettingsVersion < SettingsData.CurrentVersion;
    }

    /// <summary>Records that setup has been finished (or skipped).</summary>
    /// <param name="settings">The settings to update.</param>
    public static void Complete(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (IsNeeded(settings.Data)) settings.Update(settings.Data with { SettingsVersion = SettingsData.CurrentVersion });
    }
}

/// <summary>The text passes these settings describe.</summary>
/// <remarks>
/// Lives here rather than in the view so that the engine, the Hub and the tests all read the
/// same translation. A toggle that means one thing in Settings and another in the pipeline is
/// the sort of bug that only shows up in a user's typed text.
/// </remarks>
public static class SettingsPipeline
{
    /// <summary>Turns stored preferences into pipeline options.</summary>
    /// <param name="data">The user's settings.</param>
    /// <returns>Options with every pass switched to match.</returns>
    public static TextPipelineOptions ToOptions(this SettingsData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new TextPipelineOptions
        {
            RemoveFillers = data.RemoveFillers,
            ApplySelfCorrections = data.ApplySelfCorrections,
            EnterCommand = data.EnterCommand,
            ExpandSnippets = data.ExpandSnippets,
            FormatLists = data.FormatLists,
        };
    }
}

/// <summary>Source-generated JSON for settings.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SettingsData))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
