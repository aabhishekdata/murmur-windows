using Avalonia.Media;

namespace Murmur.App.Design;

/// <summary>
/// Decides the accent: the user's Windows accent, unless it is red.
/// </summary>
/// <remarks>
/// <para>
/// A Windows 11 app takes its accent from the system, and Murmur does — a hub in the user's own
/// colour is most of what makes it feel native. The exception is the one rule the design system
/// will not bend on either platform: <b>red means recording.</b> Windows lets the accent be red,
/// and a red Save button or a red selected tab would make the recording dot mean nothing. So a
/// red-ish accent — hue within 20° of pure red, saturated enough to read as red rather than as a
/// dusty pink or a brown — is swapped for Murmur's own indigo.
/// </para>
/// <para>
/// Indigo is also the answer when there is no system accent to follow: off Windows, and on a
/// headless test runner.
/// </para>
/// </remarks>
public static class AccentPolicy
{
    /// <summary>Murmur's own accent, and the fallback.</summary>
    public static Color Fallback { get; } = Color.FromRgb(0x5B, 0x5B, 0xD6);

    /// <summary>The fallback as drawn on a dark background.</summary>
    public static Color FallbackOnDark { get; } = Color.FromRgb(0x8A, 0x8A, 0xF0);

    /// <summary>Lower edge of the red band, in degrees. The band wraps through 0.</summary>
    public const double RedHueFrom = 340;

    /// <summary>Upper edge of the red band, in degrees.</summary>
    public const double RedHueTo = 20;

    /// <summary>Saturation above which a colour in the red band reads as red.</summary>
    public const double RedSaturation = 0.4;

    /// <summary>The accent to use for <paramref name="system"/>.</summary>
    /// <param name="system">The system accent, or null when there is none.</param>
    /// <returns>The system accent, or <see cref="Fallback"/> when it is red or absent.</returns>
    public static Color Choose(Color? system) =>
        system is { } colour && colour.A > 0 && !IsRed(colour) ? Opaque(colour) : Fallback;

    /// <summary>Whether <paramref name="colour"/> would read as red.</summary>
    /// <param name="colour">Any colour.</param>
    /// <returns>True when the hue is within the red band and it is saturated.</returns>
    public static bool IsRed(Color colour)
    {
        var (hue, saturation) = HueAndSaturation(colour);
        var inBand = hue >= RedHueFrom || hue <= RedHueTo;
        return inBand && saturation > RedSaturation;
    }

    /// <summary>The colours derived from <paramref name="accent"/>.</summary>
    /// <param name="accent">An accent already through <see cref="Choose"/>.</param>
    /// <returns>Shades for both themes, and the text that sits on each.</returns>
    public static AccentPalette Palette(Color accent)
    {
        var isFallback = accent == Fallback;

        // Windows draws accent fills with a darker shade on light backgrounds and a lighter one
        // on dark backgrounds (AccentFillColorDefault: Dark1 and Light2). The fallback keeps the
        // exact pair the design was drawn with.
        var onLight = isFallback ? Fallback : Mix(accent, Colors.Black, 0.2);
        var onDark = isFallback ? FallbackOnDark : Mix(accent, Colors.White, 0.45);

        return new AccentPalette(
            Base: accent,
            OnLightBackground: onLight,
            OnDarkBackground: onDark,
            SoftOnLight: Mix(ThemePalette.Get(ColorToken.Surface, ThemeKind.Light), onLight, 0.12),
            SoftOnDark: Mix(ThemePalette.Get(ColorToken.Surface, ThemeKind.Dark), onDark, 0.18),
            TextOnLight: ReadableOn(onLight),
            TextOnDark: ReadableOn(onDark));
    }

    /// <summary>Blends <paramref name="from"/> toward <paramref name="to"/>.</summary>
    /// <param name="from">The starting colour.</param>
    /// <param name="to">The colour blended in.</param>
    /// <param name="amount">0 keeps <paramref name="from"/>; 1 gives <paramref name="to"/>.</param>
    /// <returns>The opaque blend.</returns>
    public static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        Channel(from.R, to.R, amount), Channel(from.G, to.G, amount), Channel(from.B, to.B, amount));

    /// <summary>WCAG relative luminance.</summary>
    /// <param name="colour">Any colour; alpha is ignored.</param>
    /// <returns>0 (black) … 1 (white).</returns>
    public static double Luminance(Color colour)
    {
        static double Linear(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear(colour.R)) + (0.7152 * Linear(colour.G)) + (0.0722 * Linear(colour.B));
    }

    /// <summary>WCAG contrast ratio between two colours.</summary>
    /// <param name="a">One colour.</param>
    /// <param name="b">The other.</param>
    /// <returns>1 … 21.</returns>
    public static double Contrast(Color a, Color b)
    {
        var x = Luminance(a);
        var y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>White or black, whichever reads better on <paramref name="fill"/>.</summary>
    private static Color ReadableOn(Color fill) =>
        Contrast(fill, Colors.White) >= Contrast(fill, Colors.Black) ? Colors.White : Colors.Black;

    private static Color Opaque(Color colour) => Color.FromRgb(colour.R, colour.G, colour.B);

    private static byte Channel(byte from, byte to, double amount) =>
        (byte)Math.Clamp(Math.Round(from + ((to - from) * amount)), 0, 255);

    private static (double Hue, double Saturation) HueAndSaturation(Color colour)
    {
        var r = colour.R / 255.0;
        var g = colour.G / 255.0;
        var b = colour.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        // HSV saturation: how far from grey, independent of how light the colour is — a deep
        // crimson and a bright scarlet are equally "red".
        var saturation = max <= 0 ? 0 : delta / max;
        if (delta <= 0) return (0, saturation);

        double hue;
        if (max == r) hue = 60 * (((g - b) / delta) % 6);
        else if (max == g) hue = 60 * (((b - r) / delta) + 2);
        else hue = 60 * (((r - g) / delta) + 4);

        if (hue < 0) hue += 360;
        return (hue, saturation);
    }
}

/// <summary>An accent and the colours derived from it, for both themes.</summary>
/// <param name="Base">The accent itself — what Fluent's own controls are tinted from.</param>
/// <param name="OnLightBackground">The accent as drawn in the light theme.</param>
/// <param name="OnDarkBackground">The accent as drawn in the dark theme.</param>
/// <param name="SoftOnLight">A wash of the accent on a light surface.</param>
/// <param name="SoftOnDark">A wash of the accent on a dark surface.</param>
/// <param name="TextOnLight">Text on <paramref name="OnLightBackground"/>.</param>
/// <param name="TextOnDark">Text on <paramref name="OnDarkBackground"/>.</param>
public sealed record AccentPalette(
    Color Base,
    Color OnLightBackground,
    Color OnDarkBackground,
    Color SoftOnLight,
    Color SoftOnDark,
    Color TextOnLight,
    Color TextOnDark)
{
    /// <summary>High contrast ignores the system accent: Windows' own highlight cyan.</summary>
    public static Color HighContrastAccent { get; } = Color.FromRgb(0x1A, 0xEB, 0xFF);

    /// <summary>One accent token in one theme.</summary>
    /// <param name="token">Accent, AccentSoft or OnAccent.</param>
    /// <param name="kind">The palette.</param>
    /// <returns>The colour.</returns>
    public Color For(ColorToken token, ThemeKind kind) => (token, kind) switch
    {
        (ColorToken.Accent, ThemeKind.HighContrast) => HighContrastAccent,
        (ColorToken.AccentSoft, ThemeKind.HighContrast) => Color.FromRgb(0x00, 0x3A, 0x40),
        (ColorToken.OnAccent, ThemeKind.HighContrast) => Colors.Black,
        (ColorToken.Accent, ThemeKind.Dark) => OnDarkBackground,
        (ColorToken.AccentSoft, ThemeKind.Dark) => SoftOnDark,
        (ColorToken.OnAccent, ThemeKind.Dark) => TextOnDark,
        (ColorToken.Accent, _) => OnLightBackground,
        (ColorToken.AccentSoft, _) => SoftOnLight,
        (ColorToken.OnAccent, _) => TextOnLight,
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, "not an accent token"),
    };
}
