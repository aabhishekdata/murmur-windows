using System.Text.Json;
using System.Text.Json.Serialization;

namespace Murmur.Core;

/// <summary>User preferences.</summary>
public sealed record SettingsData
{
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
}

/// <summary>Settings, persisted as JSON.</summary>
public sealed class AppSettings
{
    private readonly string _path;

    /// <summary>Loads settings from <paramref name="path"/>, or defaults if absent.</summary>
    public AppSettings(string path)
    {
        _path = path;
        Data = Load(path);
    }

    /// <summary>The default location.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Murmur", "settings.json");

    /// <summary>Current values.</summary>
    public SettingsData Data { get; private set; }

    /// <summary>Raised after a successful save.</summary>
    public event EventHandler? Changed;

    /// <summary>Replaces and persists the settings.</summary>
    public void Update(SettingsData data)
    {
        Data = data;

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(data, SettingsJsonContext.Default.SettingsData));

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static SettingsData Load(string path)
    {
        // Corrupt or unreadable settings must never stop the app launching — defaults are
        // always a working configuration.
        try
        {
            if (!File.Exists(path)) return new SettingsData();

            return JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.SettingsData)
                   ?? new SettingsData();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsData();
        }
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
