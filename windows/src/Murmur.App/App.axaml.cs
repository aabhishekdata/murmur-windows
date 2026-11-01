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
        Styles.Insert(0, AppTheme.Create());

        // The palette, one dictionary per theme, following the Windows accent (unless it is red)
        // and high contrast — both from events, not polling.
        ThemeResources.Install(this, PlatformSettings);
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

            InstallCloudModelMenu(_composition.Settings);

            // Posted, so the bar is on screen first and the welcome window opens over it.
            if (Core.Onboarding.IsNeeded(_composition.Settings.Data))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Guarded("Onboarding", ShowOnboarding));
            }

            // Closing either window leaves Murmur running in the tray — the hotkey still works,
            // which is the whole point of a dictation app. Quit is explicit, from the tray menu
            // or the bar's own menu.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Disposing tears down the keyboard hook and releases the audio device. Leaving
            // a low-level hook installed after exit is the kind of thing that makes a
            // machine feel broken until it is rebooted.
            //
            // Both events, because neither covers every exit. Shutdown() — what Quit calls — is
            // a forced shutdown and never raises ShutdownRequested, which comes only from
            // TryShutdown or the OS ending the session; Exit is raised on every path. Without
            // this, a change still waiting on the settings' save delay — very often the bar's
            // final position — is lost on Quit.
            desktop.ShutdownRequested += (_, _) => DisposeComposition();
            desktop.Exit += (_, _) => DisposeComposition();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Every tray and bar handler is wrapped: an unhandled exception on the UI thread takes the
    // whole tray-resident process down with no window left to show an error, which is the worst
    // possible failure mode. Log to last-crash.log and stay alive instead.

    private void OnTrayShow(object? sender, EventArgs e) => Guarded("Tray 'Show Murmur'", ShowHub);

    private void OnTrayBar(object? sender, EventArgs e) => Guarded("Tray 'Show bar'", ShowBar);

    // Copies rather than pastes: clicking the tray has just taken focus from the window the text
    // was meant for. The bar owns the clipboard call, so the tray and the bar's menu behave alike.
    private void OnTrayCopyLast(object? sender, EventArgs e) =>
        Guarded("Tray 'Copy last transcript'", () => _ = _bar?.CopyLastAsync());

    private void OnTraySettings(object? sender, EventArgs e) => Guarded("Tray 'Settings…'", ShowSettings);

    private void OnTrayQuit(object? sender, EventArgs e) => Guarded("Tray 'Quit'", Quit);

    /// <summary>
    /// Adds the "Cloud model: Accurate / Fast" switch to the tray menu.
    /// </summary>
    /// <remarks>
    /// Built here rather than in the XAML because its items follow the settings: the tick moves
    /// to whichever profile is in use, and a profile with no deployment is disabled. The choice
    /// applies to the next dictation with nothing reloaded.
    /// </remarks>
    private void InstallCloudModelMenu(Core.AppSettings settings)
    {
        if (TrayIcon.GetIcons(this) is not { Count: > 0 } icons || icons[0].Menu is not { } menu) return;

        var accurate = new NativeMenuItem { ToggleType = MenuItemToggleType.Radio };
        var fast = new NativeMenuItem { ToggleType = MenuItemToggleType.Radio };
        accurate.Click += (_, _) => Guarded("Tray 'Cloud model: Accurate'",
            () => AzureProfiles.Select(settings, Core.CloudProfile.Accurate));
        fast.Click += (_, _) => Guarded("Tray 'Cloud model: Fast'",
            () => AzureProfiles.Select(settings, Core.CloudProfile.Fast));

        var cloud = new NativeMenuItem
        {
            Header = "Cloud model",
            Menu = new NativeMenu { Items = { accurate, fast } },
        };

        void Refresh()
        {
            var data = settings.Data;
            var active = AzureProfiles.EffectiveProfile(data);
            foreach (var (item, profile) in new[] { (accurate, Core.CloudProfile.Accurate), (fast, Core.CloudProfile.Fast) })
            {
                var deployed = AzureProfiles.DeploymentFor(data, profile) is not null;
                item.Header = $"{AzureProfiles.Label(profile)} ({AzureProfiles.ModelFor(profile)})";
                item.IsEnabled = deployed;
                item.IsChecked = deployed && profile == active;
            }
        }

        Refresh();
        settings.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Guarded("Tray menu refresh", Refresh));

        // Just above the separator before Quit, with the other things that change how dictation behaves.
        var index = Math.Max(0, menu.Items.Count - 2);
        menu.Items.Insert(index, cloud);
    }

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

        var settings = new SettingsWindow(
            _composition.Settings, _composition.Microphones, _composition.Cloud, _composition.Models, _composition.PasteLast,
            _composition.PromptHotkeys);

        // Shown as a dialog over the hub when there is one, and standalone otherwise — the bar
        // is frameless and never activated, so it cannot own a modal dialog.
        if (_hub is not null && _hub.IsVisible) _ = settings.ShowDialog(_hub);
        else settings.Show();
    }

    private void ShowOnboarding()
    {
        if (_composition is null) return;

        var onboarding = new OnboardingWindow(_composition);
        onboarding.Show();
        onboarding.Activate();
    }

    private void Quit()
    {
        // Written before the lifetime starts closing windows, so a pending change is on disk
        // even if something later in the shutdown fails. The rest is torn down on Exit, once
        // the windows that subscribe to the engine have closed.
        _composition?.Settings.Flush();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    // Idempotent: whichever exit path runs first does the work.
    private void DisposeComposition()
    {
        var composition = _composition;
        _composition = null;
        composition?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
