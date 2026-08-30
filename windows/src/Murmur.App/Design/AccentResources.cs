using Avalonia;
using Avalonia.Media;

namespace Murmur.App.Design;

/// <summary>
/// Retints the Fluent theme's built-in accent to Murmur's.
/// </summary>
/// <remarks>
/// A handful of controls — the check box, the text-selection highlight, the focus adorner —
/// are drawn by the Fluent theme from <c>SystemAccentColor</c> rather than by anything in this
/// codebase. Left alone they come out Windows blue, which is a second accent colour in an
/// interface whose design system says there is exactly one. Overriding the resource is a
/// three-line fix; restyling every Fluent control is not.
/// </remarks>
public static class AccentResources
{
    /// <summary>Installs the accent overrides into <paramref name="application"/>.</summary>
    /// <param name="application">The application whose resources to retint.</param>
    public static void Apply(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        // Fixed values rather than the theme-aware token: these feed a control's own light and
        // dark variants, and Fluent derives the shades it needs from them.
        var accent = Color.FromRgb(0x5B, 0x5B, 0xD6);

        application.Resources["SystemAccentColor"] = accent;
        application.Resources["SystemAccentColorLight1"] = Shift(accent, 1.12);
        application.Resources["SystemAccentColorLight2"] = Shift(accent, 1.24);
        application.Resources["SystemAccentColorLight3"] = Shift(accent, 1.36);
        application.Resources["SystemAccentColorDark1"] = Shift(accent, 0.88);
        application.Resources["SystemAccentColorDark2"] = Shift(accent, 0.76);
        application.Resources["SystemAccentColorDark3"] = Shift(accent, 0.64);
    }

    private static Color Shift(Color colour, double factor) => Color.FromRgb(
        Channel(colour.R, factor), Channel(colour.G, factor), Channel(colour.B, factor));

    private static byte Channel(byte value, double factor) =>
        (byte)Math.Clamp(Math.Round(value * factor), 0, 255);
}
