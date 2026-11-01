using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Murmur.App.Design;

/// <summary>
/// Puts the palette into the application's resources, one dictionary per theme, so every view
/// that binds to a token repaints the moment the theme or the accent changes.
/// </summary>
/// <remarks>
/// <para>
/// Views bind with <see cref="Tokens.Live"/> — a dynamic-resource binding made in code — rather
/// than reading a colour once when they are built. Switching Windows between light and dark used
/// to leave every open window in the old palette until it was closed and rebuilt; now it is one
/// resource lookup per bound property, done by Avalonia, with nothing rebuilt.
/// </para>
/// <para>
/// Three dictionaries: Light, Dark and <see cref="HighContrast"/>. High contrast is Murmur's own
/// variant layered on Dark rather than FluentAvalonia's: FluentAvalonia fills its high-contrast
/// dictionary from the system only when it is allowed to follow the system theme itself, and
/// otherwise draws placeholder colours. Inheriting Dark gives every Fluent control a legible dark
/// rendering, and the Murmur tokens on top make the surfaces solid black and the text white.
/// </para>
/// <para>
/// Two things are watched on the platform, both through <c>ColorValuesChanged</c>, which costs
/// nothing until Windows raises it: the accent (see <see cref="AccentPolicy"/>) and the contrast
/// preference. No timer, no polling.
/// </para>
/// </remarks>
public static class ThemeResources
{
    /// <summary>The high-contrast variant. Resources not found in it come from Dark.</summary>
    public static ThemeVariant HighContrast { get; } = new("MurmurHighContrast", ThemeVariant.Dark);

    private static readonly ThemeVariant[] Variants = [ThemeVariant.Light, ThemeVariant.Dark, HighContrast];

    private static AccentPalette s_accent = AccentPolicy.Palette(AccentPolicy.Fallback);

    /// <summary>The accent in use, after <see cref="AccentPolicy"/>.</summary>
    public static AccentPalette Accent => s_accent;

    /// <summary>The resource key a token is published under.</summary>
    /// <param name="token">The role.</param>
    /// <returns>A string key, unique to Murmur.</returns>
    public static string Key(ColorToken token) => "Murmur." + token;

    /// <summary>Which palette <paramref name="variant"/> draws with.</summary>
    /// <param name="variant">A theme variant, or null for light.</param>
    /// <returns>The palette.</returns>
    public static ThemeKind KindOf(ThemeVariant? variant)
    {
        for (var v = variant; v is not null; v = v.InheritVariant)
        {
            if (v == HighContrast) return ThemeKind.HighContrast;
            if (v == ThemeVariant.Dark) return ThemeKind.Dark;
            if (v == ThemeVariant.Light) return ThemeKind.Light;
        }

        return ThemeKind.Light;
    }

    /// <summary>The palette the application is showing now.</summary>
    public static ThemeKind CurrentKind => KindOf(Application.Current?.ActualThemeVariant);

    /// <summary>A token's colour in <paramref name="kind"/>, with the current accent.</summary>
    /// <param name="token">The role.</param>
    /// <param name="kind">The palette.</param>
    /// <returns>The colour.</returns>
    public static Color Resolve(ColorToken token, ThemeKind kind) =>
        ThemePalette.IsAccent(token) ? s_accent.For(token, kind) : ThemePalette.Get(token, kind);

    /// <summary>
    /// Installs the palette into <paramref name="application"/> and follows the platform.
    /// </summary>
    /// <param name="application">The application.</param>
    /// <param name="platform">
    /// Where the accent and the contrast preference come from, or null to use indigo and the
    /// ordinary light/dark variants.
    /// </param>
    public static void Install(Application application, IPlatformSettings? platform)
    {
        ArgumentNullException.ThrowIfNull(application);

        foreach (var variant in Variants)
        {
            application.Resources.ThemeDictionaries[variant] = Build(KindOf(variant));
        }

        // Avalonia's placeholder settings report Windows 10's default blue as if it were a choice.
        // Only a real platform — Win32's settings derive from the placeholder, so the exact type
        // is what tells them apart — has an accent worth following.
        var real = platform is not null && platform.GetType() != typeof(DefaultPlatformSettings);
        var values = real ? platform!.GetColorValues() : null;

        // The dictionaries were just rebuilt, so the accent has to be written into them again
        // even if it has not changed.
        s_accentApplied = false;
        ApplyAccent(application, values?.AccentColor1);
        ApplyContrast(application, values?.ContrastPreference == ColorContrastPreference.High);

        if (real)
        {
            platform!.ColorValuesChanged += (_, changed) => Dispatcher.UIThread.Post(() =>
            {
                ApplyAccent(application, changed.AccentColor1);
                ApplyContrast(application, changed.ContrastPreference == ColorContrastPreference.High);
            });
        }
    }

    /// <summary>
    /// Re-tints everything that uses the accent, in place.
    /// </summary>
    /// <param name="application">The application.</param>
    /// <param name="system">The system accent, or null for none.</param>
    /// <remarks>
    /// Replacing a resource raises <c>ResourcesChanged</c> down the whole tree, so bound brushes
    /// update without a single view being rebuilt.
    /// </remarks>
    public static void ApplyAccent(Application application, Color? system)
    {
        ArgumentNullException.ThrowIfNull(application);

        var palette = AccentPolicy.Palette(AccentPolicy.Choose(system));
        if (palette == s_accent && s_accentApplied) return;

        s_accent = palette;
        s_accentApplied = true;
        BrushCache.Clear();

        foreach (var variant in Variants)
        {
            if (!application.Resources.ThemeDictionaries.TryGetValue(variant, out var provider) ||
                provider is not ResourceDictionary dictionary)
            {
                continue;
            }

            var kind = KindOf(variant);
            foreach (var token in AccentTokens) dictionary[Key(token)] = Brush(Resolve(token, kind));

            // The Fluent controls' own accent: the chosen one, except in high contrast, which
            // keeps Windows' highlight cyan whatever the accent is.
            AccentResources.Apply(dictionary, kind == ThemeKind.HighContrast ? AccentPalette.HighContrastAccent : palette.Base);
        }
    }

    private static readonly ColorToken[] AccentTokens = [ColorToken.Accent, ColorToken.AccentSoft, ColorToken.OnAccent];

    private static bool s_accentApplied;

    /// <summary>Switches high contrast on or off.</summary>
    /// <param name="application">The application.</param>
    /// <param name="high">Whether Windows asks for high contrast.</param>
    /// <remarks>
    /// Off means "follow the system light/dark setting", which is what <c>Default</c> does.
    /// </remarks>
    public static void ApplyContrast(Application application, bool high)
    {
        ArgumentNullException.ThrowIfNull(application);

        var wanted = high ? HighContrast : ThemeVariant.Default;
        if (application.RequestedThemeVariant != wanted) application.RequestedThemeVariant = wanted;
    }

    private static ResourceDictionary Build(ThemeKind kind)
    {
        var dictionary = new ResourceDictionary();

        foreach (var token in Enum.GetValues<ColorToken>())
        {
            dictionary[Key(token)] = Brush(Resolve(token, kind));
        }

        // The navigation view's content layer is the same layer every page sits on, so it takes
        // the token rather than FluentAvalonia's own value.
        dictionary["NavigationViewContentBackground"] = Brush(Resolve(ColorToken.Layer, kind));
        dictionary["NavigationViewContentGridBorderBrush"] = Brush(Resolve(ColorToken.Line, kind));

        // FluentAvalonia's setting cards and Murmur's own cards sit side by side on every page;
        // one fill and one hairline for both, or the two kinds of card are visibly different.
        dictionary["CardBackgroundFillColorDefaultBrush"] = Brush(Resolve(ColorToken.Surface, kind));
        dictionary["CardStrokeColorDefaultBrush"] = Brush(Resolve(ColorToken.Line, kind));

        if (kind == ThemeKind.HighContrast)
        {
            // The Fluent controls draw from Dark; these are the fills, strokes and text that are
            // too soft there to count as high contrast. Solid black, white edges, white text.
            var white = Brush(Colors.White);
            var black = Brush(Colors.Black);
            foreach (var key in HighContrastWhite) dictionary[key] = white;
            foreach (var key in HighContrastBlack) dictionary[key] = black;

            // Toggles, the accent button and the selection pill take Windows' highlight cyan,
            // with black on it, rather than a pastel of the everyday accent.
            var highlight = Brush(AccentPalette.HighContrastAccent);
            foreach (var key in HighContrastHighlight) dictionary[key] = highlight;
            dictionary["TextOnAccentFillColorPrimaryBrush"] = black;
            dictionary["AccentButtonForeground"] = black;
            dictionary["AccentButtonForegroundPointerOver"] = black;
            dictionary["AccentButtonForegroundPressed"] = black;
            dictionary["ToggleSwitchKnobFillOn"] = black;
            dictionary["ToggleSwitchKnobFillOnPointerOver"] = black;
            dictionary["ToggleSwitchKnobFillOnPressed"] = black;
        }

        return dictionary;
    }

    private static readonly string[] HighContrastWhite =
    [
        "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush",
        "ControlStrokeColorDefaultBrush", "ControlStrokeColorSecondaryBrush", "CardStrokeColorDefaultBrush",
        "ExpanderHeaderBorderBrush", "ExpanderContentBorderBrush", "DividerStrokeColorDefaultBrush",
        "TextControlBorderBrush", "ComboBoxBorderBrush", "ButtonBorderBrush",
    ];

    private static readonly string[] HighContrastBlack =
    [
        "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush",
        "SolidBackgroundFillColorBaseBrush", "ExpanderBackground", "ExpanderHeaderBackground",
        "ExpanderContentBackground", "ControlFillColorDefaultBrush", "ControlFillColorSecondaryBrush",
        "TextControlBackground", "ComboBoxBackground", "ButtonBackground", "LayerFillColorDefaultBrush",
        "NavigationViewDefaultPaneBackground", "NavigationViewExpandedPaneBackground",
    ];

    private static readonly string[] HighContrastHighlight =
    [
        "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
        "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
        "NavigationViewSelectionIndicatorForeground",
        "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
    ];

    private static ImmutableSolidColorBrush Brush(Color colour) => new(colour);
}
