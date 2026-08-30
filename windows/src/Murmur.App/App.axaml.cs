using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Murmur.App.Design;
using Murmur.App.Views;

namespace Murmur.App;

/// <summary>The application.</summary>
/// <remarks>
/// <para>
/// Two windows and a tray icon. The <see cref="FlowBarWindow"/> is the primary interface — a
/// small floating pill that sits above whatever the user is actually working in — and the
/// <see cref="HubWindow"/> is opened from it when there is something to manage.
/// </para>
/// <para>
/// The bar is the lifetime's <c>MainWindow</c> rather than the hub, deliberately: it is the
/// window that exists for the whole session, and with
/// <c>ShutdownMode.OnExplicitShutdown</c> neither one closing ends the process.
/// </para>
/// </remarks>
public partial class App : Application
{
    private Composition? _composition;
    private FlowBarWindow? _bar;
    private HubWindow? _hub;

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        AccentResources.Apply(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _composition = Composition.Create();

            _bar = new FlowBarWindow(_composition);
            _bar.HubRequested += (_, _) => Guarded("Open Murmur", ShowHub);
            _bar.SettingsRequested += (_, _) => Guarded("Settings…", ShowSettings);
            _bar.QuitRequested += (_, _) => Guarded("Quit", Quit);

            desktop.MainWindow = _bar;

            // Closing either window leaves Murmur running in the tray — the hotkey still works,
            // which is the whole point of a dictation app. Quit is explicit, from the tray menu
            // or the bar's own menu.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Disposing tears down the keyboard hook and releases the audio device. Leaving
            // a low-level hook installed after exit is the kind of thing that makes a
            // machine feel broken until it is rebooted.
            desktop.ShutdownRequested += (_, _) =>
            {
                _composition?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _composition = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Every tray and bar handler is wrapped: an unhandled exception on the UI thread takes the
    // whole tray-resident process down with no window left to show an error, which is the worst
    // possible failure mode. Log to last-crash.log and stay alive instead.

    private void OnTrayShow(object? sender, EventArgs e) => Guarded("Tray 'Show Murmur'", ShowHub);

    private void OnTrayBar(object? sender, EventArgs e) => Guarded("Tray 'Show bar'", ShowBar);

    private void OnTraySettings(object? sender, EventArgs e) => Guarded("Tray 'Settings…'", ShowSettings);

    private void OnTrayQuit(object? sender, EventArgs e) => Guarded("Tray 'Quit'", Quit);

    private static void Guarded(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            CrashLog.Write($"{what} failed", ex);
        }
    }

    private void ShowHub()
    {
        if (_composition is null) return;

        // Built on first use rather than at startup: the hub reads the whole transcript history
        // to build its list, and a tray app that is never opened should not pay for that.
        _hub ??= new HubWindow(_composition);

        _hub.Show();
        _hub.WindowState = WindowState.Normal;
        _hub.Activate();
    }

    private void ShowBar() => _bar?.ShowBar();

    private void ShowSettings()
    {
        if (_composition is null) return;

        var settings = new SettingsWindow(_composition.Settings, _composition.Microphones);

        // Shown as a dialog over the hub when there is one, and standalone otherwise — the bar
        // is frameless and never activated, so it cannot own a modal dialog.
        if (_hub is not null && _hub.IsVisible) _ = settings.ShowDialog(_hub);
        else settings.Show();
    }

    private void Quit()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
