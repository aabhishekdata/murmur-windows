using Avalonia.Styling;
using FluentAvalonia.Styling;

namespace Murmur.App.Design;

/// <summary>
/// The control theme every window is drawn with.
/// </summary>
/// <remarks>
/// <para>
/// FluentAvalonia's theme rather than Avalonia's <c>FluentTheme</c>: it restyles every stock
/// control to the Windows 11 look, and it is what draws FluentAvalonia's own controls —
/// NavigationView, SettingsExpander, InfoBar. Built in code rather than declared in
/// <c>App.axaml</c> so the headless tests install exactly the same theme as the app; a second
/// copy in XAML would drift.
/// </para>
/// <para>
/// Two of its three platform hooks are off on purpose. The light/dark variant already follows
/// the system through <c>RequestedThemeVariant</c>, and high contrast through
/// <see cref="ThemeResources"/>. The user's accent is applied by <see cref="AccentResources"/>
/// rather than by the theme, because Windows lets the accent be red and red means recording —
/// <see cref="AccentPolicy"/> follows the accent except then.
/// </para>
/// <para>
/// The system font is on: Segoe UI Variable is what makes a Windows 11 window look like one. Off
/// Windows — a headless test runner — the theme keeps its default and <see cref="Tokens.Fonts"/>
/// falls back to the embedded Inter.
/// </para>
/// </remarks>
internal static class AppTheme
{
    /// <summary>A new instance of the theme, configured as above.</summary>
    public static Styles Create() => new FluentAvaloniaTheme
    {
        PreferSystemTheme = false,
        PreferUserAccentColor = false,
        UseSystemFontOnWindows = true,
    };
}
