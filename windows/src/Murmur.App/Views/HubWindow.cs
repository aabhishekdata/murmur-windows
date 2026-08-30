using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>Which section of the hub is showing.</summary>
public enum HubSection
{
    /// <summary>Statistics and transcript history.</summary>
    Home,

    /// <summary>Custom words and corrections.</summary>
    Dictionary,

    /// <summary>Voice-triggered text expansions.</summary>
    Snippets,

    /// <summary>Preferences.</summary>
    Settings,
}

/// <summary>
/// The hub — where you manage Murmur, as opposed to use it.
/// </summary>
/// <remarks>
/// <para>
/// A sidebar and a content pane. Deliberately unremarkable: this window is opened
/// occasionally, to read your own words back or to teach the dictionary a name, and everything
/// in it should be findable without being looked for. The interesting surface is the floating
/// bar; this is the filing cabinet behind it.
/// </para>
/// <para>
/// Built in code rather than XAML, as the rest of the app is. Every value comes from
/// <see cref="Tokens"/>, and XAML makes it far too easy to type a literal <c>Margin="12,8"</c>
/// that silently escapes the design system. In C# a stray number is visible in review.
/// </para>
/// <para>
/// Sections are built once and reused. Rebuilding on every switch would drop the user's search
/// text each time they changed tabs.
/// </para>
/// </remarks>
public sealed class HubWindow : Window
{
    /// <summary>Sidebar width. Wide enough for the longest section name, and no wider.</summary>
    public const double SidebarWidth = 196;

    private readonly Composition? _composition;
    private readonly ContentControl _host;
    private readonly NavItem _homeNav;
    private readonly NavItem _dictionaryNav;
    private readonly NavItem _snippetsNav;
    private readonly NavItem _settingsNav;

    private DockPanel? _homeView;
    private DockPanel? _dictionaryView;
    private DockPanel? _snippetsView;
    private DockPanel? _settingsView;

    /// <summary>Builds a hub with no engine behind it. Used by headless tests and previews.</summary>
    public HubWindow() : this(null) { }

    /// <summary>Builds the hub over <paramref name="composition"/>.</summary>
    /// <param name="composition">The object graph, or null for a preview.</param>
    public HubWindow(Composition? composition)
    {
        _composition = composition;

        Title = "Murmur";
        MinWidth = 720;
        MinHeight = 520;
        Width = 940;
        Height = 660;
        Background = Tokens.Brushes.Canvas;

        _homeNav = new NavItem("⌂", "Home");
        _dictionaryNav = new NavItem("A", "Dictionary");
        _snippetsNav = new NavItem("¶", "Snippets");
        _settingsNav = new NavItem("⚙", "Settings");

        _homeNav.Click += (_, _) => ShowSection(HubSection.Home);
        _dictionaryNav.Click += (_, _) => ShowSection(HubSection.Dictionary);
        _snippetsNav.Click += (_, _) => ShowSection(HubSection.Snippets);
        _settingsNav.Click += (_, _) => ShowSection(HubSection.Settings);

        _host = new ContentControl();

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Panels.Docked(BuildSidebar(), Dock.Left),
                _host,
            },
        };

        ShowSection(HubSection.Home);
    }

    /// <summary>Which section is showing. Exposed for headless tests.</summary>
    public HubSection Section { get; private set; } = HubSection.Home;

    /// <summary>
    /// Switches sections.
    /// </summary>
    /// <param name="section">The section to show.</param>
    /// <remarks>
    /// Not called <c>Show</c>: that would overload <see cref="Window.Show()"/>, and a one-line
    /// call that means "put this window on screen" quietly becoming "switch to the Home tab"
    /// is not a mistake anyone finds by reading.
    /// </remarks>
    public void ShowSection(HubSection section)
    {
        Section = section;

        _homeNav.IsSelected = section == HubSection.Home;
        _dictionaryNav.IsSelected = section == HubSection.Dictionary;
        _snippetsNav.IsSelected = section == HubSection.Snippets;
        _settingsNav.IsSelected = section == HubSection.Settings;

        _host.Content = section switch
        {
            HubSection.Dictionary => _dictionaryView ??= BuildDictionary(),
            HubSection.Snippets => _snippetsView ??= BuildSnippets(),
            HubSection.Settings => _settingsView ??= BuildSettings(),
            _ => _homeView ??= BuildHome(),
        };
    }

    private Border BuildSidebar()
    {
        var wordmark = new StackPanel
        {
            Spacing = Tokens.Space.Hair,
            Margin = new Thickness(Tokens.Space.Base, Tokens.Space.Snug, Tokens.Space.Base, Tokens.Space.Wide),
            Children =
            {
                new TextBlock
                {
                    Text = "Murmur",
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Subtitle,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Tokens.Brushes.Ink,
                },
                new Caption { Text = "Offline dictation" },
            },
        };

        var nav = new StackPanel
        {
            Spacing = Tokens.Space.Hair,
            Children = { _homeNav, _dictionaryNav, _snippetsNav, _settingsNav },
        };

        var hint = new StackPanel
        {
            Spacing = Tokens.Space.Tight,
            Margin = new Thickness(Tokens.Space.Base, Tokens.Space.Wide, Tokens.Space.Base, Tokens.Space.Snug),
            Children =
            {
                new Caption { Text = "Push to talk", IsHeader = true },
                Panels.Note("Hold Right Ctrl anywhere, speak, release. The text lands in "
                          + "whatever has focus."),
            },
        };

        return new Border
        {
            Width = SidebarWidth,
            Background = Tokens.Brushes.SurfaceMuted,
            BorderBrush = Tokens.Brushes.Line,
            BorderThickness = new Thickness(0, 0, Tokens.Border.Hairline, 0),
            Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Wide),
            Child = new DockPanel
            {
                LastChildFill = false,
                Children =
                {
                    Panels.Docked(wordmark, Dock.Top),
                    Panels.Docked(nav, Dock.Top),
                    Panels.Docked(hint, Dock.Bottom),
                },
            },
        };
    }

    private DockPanel BuildHome()
    {
        if (_composition is null)
        {
            return WithHeader("Home", null, Panels.EmptyState(
                "No recordings yet",
                "Hold the push-to-talk key and speak. What you dictate shows up here."));
        }

        var body = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Panels.Docked(new StatsStrip(_composition.Transcripts), Dock.Top),
                Panels.Docked(
                    new SuggestionsView(
                        _composition.Settings, _composition.Dictionary, _composition.Transcripts),
                    Dock.Top),
                new TranscriptionsView(_composition.Transcripts, _composition.Dictionary),
            },
        };

        Control? banner = Composition.IsModelInstalled
            ? null
            : Panels.Notice(
                "Speech model not installed — Murmur cannot transcribe yet. "
                + "See Settings → Model.",
                BuildModelAction());

        return WithHeader("Home", banner, body);
    }

    private SoftButton BuildModelAction()
    {
        var open = Panels.OutlineButton("Open Settings");
        open.Click += (_, _) => ShowSection(HubSection.Settings);
        return open;
    }

    private DockPanel BuildDictionary() =>
        _composition is null
            ? WithHeader("Dictionary", null, Panels.EmptyState(
                "Dictionary empty",
                "Add the words it keeps getting wrong, and the spellings you want instead."))
            : WithHeader("Dictionary", null, new DictionaryView(_composition.Dictionary));

    private DockPanel BuildSnippets() =>
        _composition is null
            ? WithHeader("Snippets", null, Panels.EmptyState(
                "No snippets yet",
                "Store a block of text behind a spoken phrase — an address, a signature, "
                + "a standard reply — and say the phrase to type all of it."))
            : WithHeader("Snippets", null, new SnippetsView(_composition.Snippets));

    private DockPanel BuildSettings() =>
        _composition is null
            ? WithHeader("Settings", null, Panels.EmptyState("Settings", "Nothing to configure in a preview."))
            : WithHeader("Settings", null, new SettingsView(_composition.Settings, _composition.Microphones));

    /// <summary>Wraps a section in its title and, if there is one, a standing notice.</summary>
    private static DockPanel WithHeader(string title, Control? banner, Control body)
    {
        var head = new StackPanel
        {
            Spacing = Tokens.Space.Base,
            Margin = new Thickness(
                Tokens.Space.Wide, Tokens.Space.Wide, Tokens.Space.Wide, Tokens.Space.Roomy),
            Children = { Panels.Title(title) },
        };

        if (banner is not null) head.Children.Add(banner);

        return new DockPanel
        {
            LastChildFill = true,
            Children = { Panels.Docked(head, Dock.Top), body },
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Closing the hub leaves Murmur running in the tray and the bar floating — the hotkey
    /// still works, which is the whole point of a dictation app. Genuinely closing an Avalonia
    /// window destroys it and <c>Show()</c> on a destroyed window throws, which is exactly what
    /// used to crash the app when the tray menu reopened it. So a user-initiated close is a
    /// hide; application shutdown still closes for real. Windows built without a composition
    /// (headless tests) keep plain close semantics.
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_composition is not null && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}

/// <summary>
/// A summary strip: words dictated, recordings kept, speaking rate, streak.
/// </summary>
/// <remarks>
/// Refreshed from the store's <c>Changed</c> event, which can fire from any thread — the store
/// does no marshalling of its own. Callers are expected to marshal, and <c>Composition</c>
/// does, but this rebuilds live controls and so defends itself as well. That defence is not
/// theoretical: a background-thread refresh throwing inside a subscriber is the exact shape of
/// the bug that once silently blocked text injection.
/// </remarks>
public sealed class StatsStrip : UserControl
{
    private readonly TranscriptStore _store;
    private readonly StackPanel _row;

    /// <summary>Builds the strip over <paramref name="store"/>.</summary>
    /// <param name="store">Transcript history.</param>
    public StatsStrip(TranscriptStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Page,
        };

        Content = Panels.Card(_row);
        Margin = new Thickness(Tokens.Space.Wide, 0, Tokens.Space.Wide, Tokens.Space.Roomy);

        _store.Changed += OnStoreChanged;
        Refresh();
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        var stats = TranscriptStats.Summarise(_store.Records, DateOnly.FromDateTime(DateTime.Now));

        _row.Children.Clear();
        _row.Children.Add(Panels.Stat(stats.Words.ToString("N0", Culture), "Words dictated"));
        _row.Children.Add(Panels.Stat(stats.Recordings.ToString("N0", Culture), "Recordings"));
        _row.Children.Add(Panels.Stat(stats.WordsPerMinute.ToString("N0", Culture), "Words per minute"));
        _row.Children.Add(Panels.Stat(stats.StreakDays.ToString("N0", Culture), "Day streak"));
    }

    private static System.Globalization.CultureInfo Culture =>
        System.Globalization.CultureInfo.CurrentCulture;
}
