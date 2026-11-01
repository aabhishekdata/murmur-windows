using Avalonia.Media;

namespace Murmur.App.Design;

/// <summary>
/// Retints the Fluent theme's built-in accent to the one <see cref="AccentPolicy"/> chose.
/// </summary>
/// <remarks>
/// A handful of controls — the toggle switch, the text-selection highlight, the focus adorner,
/// the navigation pane's selection pill — are drawn by the Fluent theme from
/// <c>SystemAccentColor</c> rather than by anything in this codebase. The theme's own hook for the
/// user's accent is off (see <see cref="AppTheme"/>) because it would happily make all of those
/// red; setting the resources here instead is what lets the accent follow Windows <i>except</i>
/// when it is red. Application resources win over a theme's.
/// </remarks>
public static class AccentResources
{
    /// <summary>Installs <paramref name="accent"/> and its shades into <paramref name="resources"/>.</summary>
    /// <param name="resources">
    /// One of the application's theme dictionaries — per theme, so high contrast can carry its
    /// own accent. Application resources win over a theme's.
    /// </param>
    /// <param name="accent">The accent, already through <see cref="AccentPolicy.Choose"/>.</param>
    public static void Apply(IDictionary<object, object?> resources, Color accent)
    {
        ArgumentNullException.ThrowIfNull(resources);

        // Fluent derives the shades it needs for each theme from these: Dark1 for fills in light,
        // Light2 in dark.
        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorLight1"] = AccentPolicy.Mix(accent, Colors.White, 0.25);
        resources["SystemAccentColorLight2"] = AccentPolicy.Mix(accent, Colors.White, 0.45);
        resources["SystemAccentColorLight3"] = AccentPolicy.Mix(accent, Colors.White, 0.62);
        resources["SystemAccentColorDark1"] = AccentPolicy.Mix(accent, Colors.Black, 0.2);
        resources["SystemAccentColorDark2"] = AccentPolicy.Mix(accent, Colors.Black, 0.38);
        resources["SystemAccentColorDark3"] = AccentPolicy.Mix(accent, Colors.Black, 0.55);
    }
}
