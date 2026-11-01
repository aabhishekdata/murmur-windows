using Avalonia.Media;

namespace Murmur.App.Design;

/// <summary>Which of the three palettes is showing.</summary>
public enum ThemeKind
{
    /// <summary>The light Windows 11 palette.</summary>
    Light,

    /// <summary>The dark Windows 11 palette.</summary>
    Dark,

    /// <summary>
    /// Windows' high-contrast mode: solid black surfaces, white text, no translucency.
    /// </summary>
    HighContrast,
}

/// <summary>Every themed colour in the hub, by role.</summary>
/// <remarks>
/// The floating bar's colours are not here: the bar is dark in every theme and its values are
/// fixed (<see cref="Tokens.Colors.BarBody"/> and friends).
/// </remarks>
public enum ColorToken
{
    /// <summary>The window backdrop when there is no Mica to show through.</summary>
    Canvas,

    /// <summary>The content layer that sits on Mica, to the right of the navigation pane.</summary>
    Layer,

    /// <summary>Cards, rows and fields.</summary>
    Surface,

    /// <summary>A quieter fill — segmented-control wells, neutral chips, empty states.</summary>
    SurfaceMuted,

    /// <summary>A row under the pointer.</summary>
    Hover,

    /// <summary>The one hairline that separates anything from anything.</summary>
    Line,

    /// <summary>Primary text.</summary>
    Ink,

    /// <summary>Supporting text — descriptions, timestamps, counts.</summary>
    InkMuted,

    /// <summary>Placeholder and disabled text.</summary>
    InkFaint,

    /// <summary>The accent: selection, focus, the primary button. Follows the system accent.</summary>
    Accent,

    /// <summary>A wash of the accent, for selected rows and accent chips.</summary>
    AccentSoft,

    /// <summary>Text and glyphs sitting on the accent.</summary>
    OnAccent,

    /// <summary>Attention without alarm — the missing-model notice.</summary>
    Notice,

    /// <summary>A wash of <see cref="Notice"/>.</summary>
    NoticeSoft,

    /// <summary>Recording. Nothing else is red — not destructive actions, not the accent.</summary>
    Live,
}

/// <summary>
/// The fixed half of the palette: every token except the three accent ones, in each theme.
/// </summary>
/// <remarks>
/// <para>
/// Values follow Windows 11's own (the Settings app's neutrals, its caution amber), so the hub
/// sits on Mica the way a first-party window does rather than as a web page in a frame.
/// </para>
/// <para>
/// The accent tokens are computed instead — they follow the system accent through
/// <see cref="AccentPolicy"/> — and are filled in by <see cref="ThemeResources"/>. Asking this
/// table for one returns the indigo fallback, which is also what a machine with no accent of its
/// own shows.
/// </para>
/// </remarks>
public static class ThemePalette
{
    /// <summary>The colour of <paramref name="token"/> in <paramref name="kind"/>.</summary>
    /// <param name="token">The role.</param>
    /// <param name="kind">The palette.</param>
    /// <returns>The resolved colour.</returns>
    public static Color Get(ColorToken token, ThemeKind kind) => token switch
    {
        ColorToken.Canvas => Pick(kind, 0xFFF3F3F3, 0xFF202020, 0xFF000000),

        // Windows 11's LayerFillColorDefault: half-white on light Mica, a faint grey film on dark.
        // Translucent on purpose — this is what lets the material through without washing the
        // text out.
        ColorToken.Layer => Pick(kind, 0x80FFFFFF, 0x4C3A3A3A, 0xFF000000),

        ColorToken.Surface => Pick(kind, 0xFFFFFFFF, 0xFF2B2B2B, 0xFF000000),
        ColorToken.SurfaceMuted => Pick(kind, 0xFFF3F3F3, 0xFF333333, 0xFF000000),
        ColorToken.Hover => Pick(kind, 0xFFF5F5F5, 0xFF323232, 0xFF1A1A1A),
        ColorToken.Line => Pick(kind, 0xFFE5E5E5, 0xFF3A3A3A, 0xFFFFFFFF),
        ColorToken.Ink => Pick(kind, 0xFF1B1B1B, 0xFFFFFFFF, 0xFFFFFFFF),
        ColorToken.InkMuted => Pick(kind, 0xFF5F5F5F, 0xFFC8C8C8, 0xFFFFFFFF),
        ColorToken.InkFaint => Pick(kind, 0xFF8A8A8A, 0xFF9A9A9A, 0xFFC8C8C8),
        ColorToken.Notice => Pick(kind, 0xFF9D5D00, 0xFFFCE100, 0xFFFFFF00),
        ColorToken.NoticeSoft => Pick(kind, 0xFFFFF4CE, 0xFF433519, 0xFF000000),
        ColorToken.Live => Pick(kind, 0xFFD3453B, 0xFFE8635A, 0xFFFF6B6B),

        ColorToken.Accent or ColorToken.AccentSoft or ColorToken.OnAccent =>
            AccentPolicy.Palette(AccentPolicy.Fallback).For(token, kind),

        _ => throw new ArgumentOutOfRangeException(nameof(token), token, null),
    };

    /// <summary>Whether <paramref name="token"/> follows the system accent.</summary>
    /// <param name="token">The role.</param>
    /// <returns>True for the accent and the two colours derived from it.</returns>
    public static bool IsAccent(ColorToken token) =>
        token is ColorToken.Accent or ColorToken.AccentSoft or ColorToken.OnAccent;

    private static Color Pick(ThemeKind kind, uint light, uint dark, uint highContrast) =>
        Color.FromUInt32(kind switch
        {
            ThemeKind.Dark => dark,
            ThemeKind.HighContrast => highContrast,
            _ => light,
        });
}
