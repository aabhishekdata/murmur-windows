using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>
/// The fixed lists Settings and onboarding both offer, kept in one place so the two never
/// disagree about what a key is called.
/// </summary>
internal static class Choices
{
    /// <summary>
    /// The push-to-talk keys offered, in recommendation order.
    /// </summary>
    /// <remarks>
    /// Right Alt is included but listed last and carries a warning: on German, Polish, UK,
    /// Nordic and most Latin-American layouts it is AltGr, and binding push-to-talk there
    /// breaks typing <c>@</c>, <c>€</c>, <c>\</c> and <c>|</c>.
    /// </remarks>
    public static IReadOnlyList<(int Value, string Label)> Keys { get; } =
    [
        (0xA3, "Right Ctrl"),
        (0xA1, "Right Shift"),
        (0x14, "Caps Lock"),
        (0x7C, "F13"),
        (0xA5, "Right Alt"),
    ];

    /// <summary>The warning shown when Right Alt is chosen.</summary>
    public const string AltGrWarning =
        "Right Alt is AltGr on many European layouts — binding it here will interfere with typing "
        + "@, €, \\ and |.";

    /// <summary>The warning for <paramref name="key"/>, or null.</summary>
    /// <param name="key">A virtual-key code.</param>
    /// <returns>The warning text, or null when the key is safe.</returns>
    public static string? WarningFor(int key) => key == 0xA5 ? AltGrWarning : null;

    /// <summary>What a key is called, or its code when it is not one of <see cref="Keys"/>.</summary>
    /// <param name="key">A virtual-key code.</param>
    /// <returns>A label.</returns>
    public static string KeyLabel(int key)
    {
        foreach (var (value, label) in Keys)
        {
            if (value == key) return label;
        }

        return $"Key 0x{key:X2}";
    }

    /// <summary>The edges the floating bar can dock to.</summary>
    public static IReadOnlyList<(ScreenEdge Value, string Label)> Edges { get; } =
    [
        (ScreenEdge.Bottom, "Bottom centre"),
        (ScreenEdge.Left, "Left edge"),
        (ScreenEdge.Right, "Right edge"),
    ];

    /// <summary>The three engine modes, as the segmented control labels them.</summary>
    public static IReadOnlyList<(EngineMode Value, string Label)> Engines { get; } =
    [
        (EngineMode.Parakeet, "Offline (Parakeet)"),
        (EngineMode.Azure, "Azure"),
        (EngineMode.AzureWithParakeetFallback, "Azure + offline fallback"),
    ];

    /// <summary>A short name for the engine in use, for the home page.</summary>
    /// <param name="mode">The engine mode.</param>
    /// <returns>The label.</returns>
    public static string EngineLabel(EngineMode mode) => mode switch
    {
        EngineMode.Azure => "Azure OpenAI",
        EngineMode.AzureWithParakeetFallback => "Azure OpenAI, offline fallback",
        _ => "Parakeet, on this PC",
    };
}
