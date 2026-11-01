using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Murmur.App.Controls;
using Murmur.App.Design;

namespace Murmur.App.Views;

/// <summary>
/// The Windows 11 frame every Murmur window that isn't the bar shares: Mica, falling back to
/// acrylic and then to a solid canvas, and a 32 px title bar drawn by the app with Windows' own
/// caption buttons.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mica costs nothing while the window sits there.</b> It is a system backdrop that DWM
/// composes from the wallpaper, sampled once when the window is activated; it is not a blur the
/// app renders, and it does not wake the render loop. Acrylic is the fallback where Mica is not
/// offered (Windows 10), and a solid canvas where neither is — a remote desktop session, or
/// transparency effects turned off.
/// </para>
/// <para>
/// High contrast always gets the solid canvas: a material showing wallpaper through the window
/// is exactly what high contrast asks apps not to do.
/// </para>
/// <para>
/// The title bar is ours so the window's content can run up under it, as Windows Settings' does.
/// It is marked as the window's drag region, and the minimise, maximise and close buttons stay
/// the system's — snap layouts on hover included.
/// </para>
/// </remarks>
internal static class WindowChrome
{
    /// <summary>The backdrops asked for, best first.</summary>
    public static IReadOnlyList<WindowTransparencyLevel> Materials { get; } =
    [
        WindowTransparencyLevel.Mica,
        WindowTransparencyLevel.AcrylicBlur,
        WindowTransparencyLevel.None,
    ];

    /// <summary>Applies the frame to <paramref name="window"/>.</summary>
    /// <param name="window">A hub-style window.</param>
    public static void Install(Window window)
    {
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = Tokens.Chrome.TitleBarHeight;
        window.WindowDecorations = WindowDecorations.Full;
        window.FontFamily = Tokens.Fonts.Ui;
        window.FontSize = Tokens.Fonts.Body;

        // Where the compositor cannot do transparency at all, Avalonia paints this instead of
        // leaving the window black. Bound, so it follows the theme like everything else.
        window.Bind(TopLevel.TransparencyBackgroundFallbackProperty, Tokens.Live.Canvas);

        ApplyMaterial(window);
        window.ActualThemeVariantChanged += (_, _) => ApplyMaterial(window);
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ActualTransparencyLevelProperty) ApplyMaterial(window);
        };
    }

    /// <summary>
    /// Asks for Mica (or solid, in high contrast) and fills the background only when no material
    /// was granted.
    /// </summary>
    /// <param name="window">The window.</param>
    public static void ApplyMaterial(Window window)
    {
        var highContrast = ThemeResources.KindOf(window.ActualThemeVariant) == ThemeKind.HighContrast;
        var wanted = highContrast ? [WindowTransparencyLevel.None] : Materials;

        if (!window.TransparencyLevelHint.SequenceEqual(wanted)) window.TransparencyLevelHint = wanted;

        // Transparent over a granted material — the window *is* the material — and the solid
        // canvas otherwise, so a refused material never shows as a black window.
        var solid = highContrast || window.ActualTransparencyLevel == WindowTransparencyLevel.None;
        if (solid)
        {
            window.Bind(TemplatedControl.BackgroundProperty, Tokens.Live.Canvas);
        }
        else
        {
            window.ClearValue(TemplatedControl.BackgroundProperty);
            window.Background = Brushes.Transparent;
        }
    }

    /// <summary>Whether <paramref name="window"/> is showing a system material right now.</summary>
    /// <param name="window">The window.</param>
    /// <returns>True over Mica or acrylic.</returns>
    public static bool HasMaterial(Window window) =>
        window.ActualTransparencyLevel != WindowTransparencyLevel.None &&
        ThemeResources.KindOf(window.ActualThemeVariant) != ThemeKind.HighContrast;

    /// <summary>
    /// The 32 px title bar: the app's mark and the window title, on the left, as a drag region.
    /// </summary>
    /// <param name="title">The window title.</param>
    /// <returns>The bar; dock it to the top of the window.</returns>
    public static Control TitleBar(string title)
    {
        var bar = new Border
        {
            Height = Tokens.Chrome.TitleBarHeight,
            Background = Brushes.Transparent,
            Padding = new Thickness(Tokens.Space.Roomy, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.Space.Base,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new FluentIcon
                    {
                        Data = Icons.Mic,
                        VerticalAlignment = VerticalAlignment.Center,
                        [!FluentIcon.ForegroundProperty] = Tokens.Live.Accent,
                    },
                    new TextBlock
                    {
                        Text = title,
                        FontFamily = Tokens.Fonts.Ui,
                        FontSize = Tokens.Fonts.Label,
                        VerticalAlignment = VerticalAlignment.Center,
                        [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
                    },
                },
            },
        };

        // Dragging, double-click to maximise and the system menu all come from this role, and
        // the caption buttons to its right stay Windows' own.
        WindowDecorationProperties.SetElementRole(bar, WindowDecorationsElementRole.TitleBar);
        return bar;
    }
}
