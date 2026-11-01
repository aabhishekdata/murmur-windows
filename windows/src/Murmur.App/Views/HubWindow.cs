using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Abstractions;
using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>Which page of the hub is showing.</summary>
public enum HubSection
{
    /// <summary>Status, statistics, suggestions and the last few dictations.</summary>
    Home,

    /// <summary>Every transcript, searchable.</summary>
    History,

    /// <summary>Custom words and corrections.</summary>
    Dictionary,

    /// <summary>Saved prompts and voice-triggered text expansions.</summary>
    Snippets,

    /// <summary>Preferences.</summary>
    Settings,
}

/// <summary>
/// The hub — where you manage Murmur, as opposed to use it.
/// </summary>
/// <remarks>
/// <para>
/// A Windows 11 window in the shape of the Settings app: Mica behind everything, a navigation
/// pane on the left with Fluent icons, and each page on a translucent content layer with a
/// rounded top-left corner. The title bar is the app's own 32 px strip (see
/// <see cref="WindowChrome"/>), so the pane runs up to the top of the window.
/// </para>
/// <para>
/// Built in code rather than XAML, as the rest of the app is. Every value comes from
/// <see cref="Tokens"/>, and XAML makes it far too easy to type a literal <c>Margin="12,8"</c>
/// that silently escapes the design system. In C# a stray number is visible in review.
/// </para>
/// <para>
/// Pages are built once and reused. Rebuilding on every switch would drop the user's search
/// text each time they changed pages. A theme switch rebuilds nothing either: every colour is a
/// live resource binding.
/// </para>
/// </remarks>
public sealed class HubWindow : Window
{
    private readonly Composition? _composition;
    private readonly FANavigationView _navigation;
    private readonly Dictionary<HubSection, FANavigationViewItem> _items = [];
    private readonly Dictionary<HubSection, Control> _pages = [];
    private bool _syncing;

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
        Width = 1040;
        Height = 720;
        WindowChrome.Install(this);

        _navigation = new FANavigationView
        {
            PaneDisplayMode = FANavigationViewPaneDisplayMode.Left,
            OpenPaneLength = Tokens.Chrome.PaneWidth,
            IsSettingsVisible = false,
            IsBackButtonVisible = false,
            IsPaneToggleButtonVisible = false,
            IsPaneOpen = true,
            CompactModeThresholdWidth = 0,
            ExpandedModeThresholdWidth = 0,
        };

        Add(HubSection.Home, "Home", Icons.Home, footer: false);
        Add(HubSection.History, "History", Icons.History, footer: false);
        Add(HubSection.Dictionary, "Dictionary", Icons.Dictionary, footer: false);
        Add(HubSection.Snippets, "Prompts & snippets", Icons.Prompts, footer: false);
        Add(HubSection.Settings, "Settings", Icons.Settings, footer: true);

        _navigation.SelectionChanged += OnSelectionChanged;

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Panels.Docked(WindowChrome.TitleBar("Murmur"), Dock.Top),
                _navigation,
            },
        };

        ShowSection(HubSection.Home);
    }

    /// <summary>Which page is showing. Exposed for headless tests.</summary>
    public HubSection Section { get; private set; } = HubSection.Home;

    /// <summary>The navigation pane. Exposed for headless tests.</summary>
    internal FANavigationView Navigation => _navigation;

    /// <summary>
    /// Switches pages.
    /// </summary>
    /// <param name="section">The page to show.</param>
    /// <remarks>
    /// Not called <c>Show</c>: that would overload <see cref="Window.Show()"/>, and a one-line
    /// call that means "put this window on screen" quietly becoming "switch to the Home tab"
    /// is not a mistake anyone finds by reading.
    /// </remarks>
    public void ShowSection(HubSection section)
    {
        Section = section;

        // Selecting the item raises SelectionChanged, which would call back in here.
        _syncing = true;
        try
        {
            _navigation.SelectedItem = _items[section];
        }
        finally
        {
            _syncing = false;
        }

        if (!_pages.TryGetValue(section, out var page))
        {
            page = Build(section);
            _pages[section] = page;
        }

        _navigation.Content = page;
    }

    private void Add(HubSection section, string label, string icon, bool footer)
    {
        var item = new FANavigationViewItem
        {
            Content = label,
            Tag = section,
            IconSource = Panels.IconSource(icon),
        };

        _items[section] = item;
        if (footer) _navigation.FooterMenuItems.Add(item);
        else _navigation.MenuItems.Add(item);
    }

    private void OnSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (e.SelectedItem is FANavigationViewItem { Tag: HubSection section }) ShowSection(section);
    }

    private DockPanel Build(HubSection section) => section switch
    {
        HubSection.History => Page("History", null, BuildHistory(), scrolls: false),
        HubSection.Dictionary => Page("Dictionary", null, BuildDictionary(), scrolls: false),
        HubSection.Snippets => Page("Prompts & snippets", null, BuildSnippets(), scrolls: false),
        HubSection.Settings => Page("Settings", null, BuildSettings(), scrolls: true),
        _ => Page("Home", null, BuildHome(), scrolls: true),
    };

    // ---- Pages ----

    private Control BuildHome()
    {
        if (_composition is null)
        {
            return Panels.EmptyState(
                "No recordings yet",
                "Hold the push-to-talk key and speak. What you dictate shows up here.",
                Icons.Mic);
        }

        var stack = new StackPanel { Spacing = Tokens.Space.Wide };

        // A fresh look rather than the cached answer: building Home is rare, and it is where
        // someone who has just followed the model instructions comes to see if it worked.
        _composition.Models.Refresh();
        var readiness = _composition.Readiness;
        if (readiness != EngineReadiness.Ready)
        {
            var open = Panels.OutlineButton("Open Settings");
            open.Click += (_, _) => ShowSection(HubSection.Settings);
            stack.Children.Add(Panels.Notice(ReadinessSentence(readiness), open));
        }

        stack.Children.Add(new StatusCard(_composition));
        stack.Children.Add(new UsageDashboard(_composition.Transcripts, _composition.Settings));
        stack.Children.Add(new SuggestionsView(_composition.Settings, _composition.Dictionary, _composition.Transcripts));
        stack.Children.Add(new RecentDictations(_composition.Transcripts, () => ShowSection(HubSection.History)));

        return stack;
    }

    private static string ReadinessSentence(EngineReadiness readiness) => readiness switch
    {
        EngineReadiness.NoModel =>
            "Speech model not installed — Murmur cannot transcribe yet. Run get-model.ps1, or switch to Azure in Settings.",
        EngineReadiness.NoApiKey => "Azure is chosen but no API key is saved. Add one in Settings → Transcription.",
        EngineReadiness.NotConfigured => "Azure is chosen but not set up yet. Add the endpoint and a deployment in Settings.",
        EngineReadiness.KeyRejected => "Azure rejected the saved API key. Replace it in Settings → Transcription.",
        _ => string.Empty,
    };

    private Control BuildHistory() =>
        _composition is null
            ? Panels.EmptyState("No recordings yet", "Everything you dictate is kept here, newest first.", Icons.History)
            : new TranscriptionsView(_composition.Transcripts, _composition.Dictionary, _composition);

    private Control BuildDictionary() =>
        _composition is null
            ? Panels.EmptyState(
                "Dictionary empty",
                "Add the words it keeps getting wrong, and the spellings you want instead.",
                Icons.Dictionary)
            : new DictionaryView(_composition.Dictionary);

    private Control BuildSnippets() =>
        _composition is null
            ? Panels.EmptyState(
                "No prompts or snippets yet",
                "Save a prompt you use every day and say its name to paste all of it, or give "
                + "it a shortcut. Snippets put an address or a signature mid-sentence.",
                Icons.Prompts)
            : new SnippetsView(_composition.Snippets, _composition.PromptHotkeys, _composition.PasteLast);

    private Control BuildSettings() =>
        _composition is null
            ? Panels.EmptyState("Settings", "Nothing to configure in a preview.", Icons.Settings)
            : new SettingsView(
                _composition.Settings, _composition.Microphones, _composition.Cloud, _composition.Models,
                _composition.PasteLast, _composition.PromptHotkeys)
            {
                OpenPrompts = () => ShowSection(HubSection.Snippets),
            };

    /// <summary>
    /// A page: the title, an optional standing notice, and the body — on a scroller for pages that
    /// are a column of cards, bare for lists that scroll themselves.
    /// </summary>
    private static DockPanel Page(string title, Control? banner, Control body, bool scrolls)
    {
        var head = new StackPanel
        {
            Spacing = Tokens.Space.Base,
            Margin = new Thickness(Tokens.Space.Section, Tokens.Space.Wide, Tokens.Space.Section, Tokens.Space.Roomy),
            Children = { Panels.Title(title) },
        };

        if (banner is not null) head.Children.Add(banner);

        Control content = body;
        if (scrolls)
        {
            content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new Border
                {
                    Padding = new Thickness(Tokens.Space.Section, 0, Tokens.Space.Section, Tokens.Space.Section),
                    Child = Readable(body),
                },
            };
        }

        return new DockPanel
        {
            LastChildFill = true,
            Children = { Panels.Docked(head, Dock.Top), content },
        };
    }

    /// <summary>Keeps a column of cards to a readable width, left-aligned, as Settings does.</summary>
    private static Grid Readable(Control body)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(1, GridUnitType.Star) { MaxWidth = Tokens.Chrome.ContentMaxWidth },
                new ColumnDefinition(0, GridUnitType.Star),
            },
        };

        grid.Children.Add(body);
        return grid;
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
/// The top of Home: whether Murmur is ready, which engine will hear you, and the key to hold.
/// </summary>
/// <remarks>
/// The one place that answers "will it work if I press the key now, and where does my voice go?"
/// in a sentence. Follows readiness live, from the composition's one event for it.
/// </remarks>
public sealed class StatusCard : UserControl
{
    private readonly Composition _composition;
    private readonly TextBlock _headline;
    private readonly TextBlock _detail;
    private readonly FluentIcon _engineIcon;
    private readonly TextBlock _engine;

    /// <summary>Builds the card.</summary>
    /// <param name="composition">The object graph.</param>
    public StatusCard(Composition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        _composition = composition;

        _headline = Panels.Subtitle(string.Empty);
        _detail = Panels.Note(string.Empty);
        _detail.FontSize = Tokens.Fonts.Body;
        _engineIcon = new FluentIcon { VerticalAlignment = VerticalAlignment.Center, [!FluentIcon.ForegroundProperty] = Tokens.Live.InkMuted };
        _engine = new TextBlock
        {
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Label,
            VerticalAlignment = VerticalAlignment.Center,
            [!TextBlock.ForegroundProperty] = Tokens.Live.InkMuted,
        };

        var badge = new Border
        {
            CornerRadius = new CornerRadius(Tokens.Radius.Pill),
            Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Tight + 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            [!Border.BackgroundProperty] = Tokens.Live.SurfaceMuted,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.Space.Snug - Tokens.Space.Hair,
                Children = { _engineIcon, _engine },
            },
        };

        Content = Panels.Card(new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Panels.Docked(new Border
                {
                    Width = Tokens.Space.Page,
                    Height = Tokens.Space.Page,
                    CornerRadius = new CornerRadius(Tokens.Radius.Card),
                    Margin = new Thickness(0, 0, Tokens.Space.Roomy, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    [!Border.BackgroundProperty] = Tokens.Live.AccentSoft,
                    Child = new FluentIcon
                    {
                        Data = Icons.Mic,
                        Width = Tokens.Space.Wide,
                        Height = Tokens.Space.Wide,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        [!FluentIcon.ForegroundProperty] = Tokens.Live.Accent,
                    },
                }, Dock.Left),
                new StackPanel
                {
                    Spacing = Tokens.Space.Tight,
                    Children = { _headline, _detail, Spaced(badge) },
                },
            },
        });

        Refresh();
    }

    private static Control Spaced(Control control)
    {
        control.Margin = new Thickness(0, Tokens.Space.Tight, 0, 0);
        return control;
    }

    /// <summary>The headline on show. Exposed for headless tests.</summary>
    internal string Headline => _headline.Text ?? string.Empty;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _composition.ReadinessChanged += OnReadinessChanged;
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _composition.ReadinessChanged -= OnReadinessChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnReadinessChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        var data = _composition.Settings.Data;
        var key = Choices.KeyLabel(data.PushToTalkKey);
        var ready = _composition.Readiness == EngineReadiness.Ready;

        _headline.Text = ready ? "Ready when you are" : "Not ready to dictate yet";
        _detail.Text = ready
            ? $"Hold {key} anywhere, speak, and let go. The text lands wherever your cursor is."
            : $"Holding {key} won't do anything until the engine is set up.";

        var cloud = EngineStatus.UsesCloud(data.Engine);
        _engineIcon.Data = cloud ? Icons.Cloud : Icons.Laptop;
        _engine.Text = Choices.EngineLabel(data.Engine);
    }
}

/// <summary>
/// Home's usage dashboard: words, minutes of audio, time saved and the estimated cloud cost, then
/// the split by engine and the assumptions behind the estimates, which the user can change.
/// </summary>
/// <remarks>
/// <para>
/// The arithmetic is <see cref="UsageStats"/>'s, tested against fixed histories; this only lays it
/// out. "Time saved" and "cloud cost" are estimates and say so: the first rests on a typing speed,
/// the second on a per-minute price for a bill Murmur never sees. Both inputs are in the
/// "How these are worked out" card and take effect as they are typed.
/// </para>
/// <para>
/// Refreshed from the store's <c>Changed</c> event, which can fire from any thread — the store
/// does no marshalling of its own. Callers are expected to marshal, and <c>Composition</c>
/// does, but this rebuilds live controls and so defends itself as well. That defence is not
/// theoretical: a background-thread refresh throwing inside a subscriber is the exact shape of
/// the bug that once silently blocked text injection.
/// </para>
/// </remarks>
public sealed class UsageDashboard : UserControl
{
    private readonly TranscriptStore _store;
    private readonly AppSettings _settings;
    private readonly Grid _tiles;
    private readonly StackPanel _engines = new() { Spacing = Tokens.Space.Base };

    /// <summary>Builds the dashboard over <paramref name="store"/>.</summary>
    /// <param name="store">Transcript history.</param>
    /// <param name="settings">Where the typing speed and per-minute prices are kept.</param>
    public UsageDashboard(TranscriptStore store, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        _store = store;
        _settings = settings;

        // Four equal cards with the card gap between them — the tiles on Windows' own home page.
        _tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };

        Content = new StackPanel
        {
            Spacing = Tokens.Space.Tight,
            Children =
            {
                _tiles,
                Panels.Card(new StackPanel
                {
                    Spacing = Tokens.Space.Base,
                    Children = { new Caption { Text = "By engine", IsHeader = true }, _engines },
                }),
                BuildAssumptions(),
            },
        };

        _store.Changed += OnInputChanged;
        Refresh();
    }

    /// <summary>The figures on show. Exposed for headless tests.</summary>
    internal UsageSummary Summary { get; private set; } = UsageStats.Summarise([]);

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _settings.Changed -= OnInputChanged;
        _settings.Changed += OnInputChanged;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _settings.Changed -= OnInputChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnInputChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        var data = _settings.Data;
        Summary = UsageStats.Summarise(
            _store.Records, data.TypingWordsPerMinute, data.CloudPricePerMinuteAccurate, data.CloudPricePerMinuteFast);

        _tiles.Children.Clear();
        Tile(0, Summary.Words.ToString("N0", Culture), "Words dictated");
        Tile(1, Summary.AudioMinutes.ToString(Summary.AudioMinutes < 10 ? "N1" : "N0", Culture), "Minutes spoken");
        Tile(2, UsageStats.FormatDuration(Summary.TimeSaved, Culture), "Time saved (est.)");
        Tile(3, UsageStats.FormatCost(Summary.EstimatedCloudCost, Culture), "Cloud cost (est.)");

        RefreshEngines();
    }

    private void Tile(int column, string value, string label)
    {
        var card = Panels.Card(Panels.Stat(value, label));
        card.Margin = new Thickness(column == 0 ? 0 : Tokens.Space.Tight, 0, 0, 0);
        Grid.SetColumn(card, column);
        _tiles.Children.Add(card);
    }

    /// <summary>One row per engine: its name, a bar for its share of the minutes, and its counts.</summary>
    private void RefreshEngines()
    {
        _engines.Children.Clear();

        if (Summary.Dictations == 0)
        {
            _engines.Children.Add(Panels.Note("Nothing dictated yet."));
            return;
        }

        var total = Math.Max(Summary.AudioMinutes, double.Epsilon);
        foreach (var usage in Summary.ByEngine)
        {
            var share = usage.AudioMinutes / total;
            var detail = string.Format(
                Culture,
                "{0:N0} {1} · {2:N0} words · {3:N1} min",
                usage.Dictations,
                usage.Dictations == 1 ? "dictation" : "dictations",
                usage.Words,
                usage.AudioMinutes);

            var bar = new Grid
            {
                Height = Tokens.Space.Tight + Tokens.Space.Hair,
                ColumnDefinitions =
                {
                    new ColumnDefinition(Math.Max(share, 0.0001), GridUnitType.Star),
                    new ColumnDefinition(Math.Max(1 - share, 0.0001), GridUnitType.Star),
                },
            };
            bar.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(Tokens.Space.Hair + 1),
                [!Border.BackgroundProperty] = Tokens.Live.SurfaceMuted,
                [Grid.ColumnSpanProperty] = 2,
            });
            bar.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(Tokens.Space.Hair + 1),
                [!Border.BackgroundProperty] = Tokens.Live.Accent,
            });

            _engines.Children.Add(new StackPanel
            {
                Spacing = Tokens.Space.Tight,
                Children =
                {
                    new DockPanel
                    {
                        LastChildFill = true,
                        Children =
                        {
                            Panels.Docked(new Caption { Text = detail, VerticalAlignment = VerticalAlignment.Center }, Dock.Right),
                            Panels.Body(EngineName(usage.Engine)),
                        },
                    },
                    bar,
                },
            });
        }

        if (Summary.FellBack > 0)
        {
            _engines.Children.Add(Panels.Note(Summary.FellBack == 1
                ? "1 dictation was heard by Parakeet because Azure couldn't be reached."
                : $"{Summary.FellBack.ToString("N0", Culture)} dictations were heard by Parakeet because Azure couldn't be reached."));
        }
    }

    private static string EngineName(TranscriberKind engine) => engine switch
    {
        TranscriberKind.Parakeet => "Parakeet, on this PC",
        TranscriberKind.Azure => "Azure OpenAI",
        _ => "Before engines were recorded",
    };

    /// <summary>The inputs to the two estimates, editable where they are used.</summary>
    private FASettingsExpander BuildAssumptions()
    {
        var data = _settings.Data;

        var wpm = Number(data.TypingWordsPerMinute, 5, 200, 1, "0", v =>
            _settings.Update(_settings.Data with { TypingWordsPerMinute = (int)Math.Round(v) }));
        var accurate = Number(data.CloudPricePerMinuteAccurate, 0, 1, 0.001, "0.000", v =>
            _settings.Update(_settings.Data with { CloudPricePerMinuteAccurate = v }));
        var fast = Number(data.CloudPricePerMinuteFast, 0, 1, 0.001, "0.000", v =>
            _settings.Update(_settings.Data with { CloudPricePerMinuteFast = v }));

        var note = Panels.Note(
            "Time saved is what typing these words would have taken at your speed, less the time spent "
            + "saying them and waiting for the text. Cloud cost counts only minutes Azure heard, at your "
            + "prices — an estimate; your Azure bill is the real figure.");
        note.TextWrapping = TextWrapping.Wrap;

        var card = Panels.Expander(Icons.Info, "How these are worked out",
            "Your typing speed and Azure's price per minute. Both are estimates you can correct.",
            null,
            Panels.Item("Typing speed", "Words per minute. 40 is typical.", wpm),
            Panels.Item("Accurate price", "US dollars per minute of audio, gpt-4o-transcribe.", accurate),
            Panels.Item("Fast price", "US dollars per minute of audio, gpt-4o-mini-transcribe.", fast),
            Panels.Item(note));

        // Closed until asked for: the figures are the point of Home, the arithmetic is not.
        card.IsExpanded = false;
        return card;
    }

    private static NumericUpDown Number(
        double value, double minimum, double maximum, double step, string format, Action<double> onChange)
    {
        var box = new NumericUpDown
        {
            Value = (decimal)value,
            Minimum = (decimal)minimum,
            Maximum = (decimal)maximum,
            Increment = (decimal)step,
            FormatString = format,
            MinWidth = 140,
        };

        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } chosen) onChange((double)chosen);
        };

        return box;
    }

    private static CultureInfo Culture => CultureInfo.CurrentCulture;
}

/// <summary>The last few dictations on Home, with a way through to the whole history.</summary>
public sealed class RecentDictations : UserControl
{
    /// <summary>How many are shown.</summary>
    public const int Count = 3;

    private readonly TranscriptStore _store;
    private readonly StackPanel _list = new() { Spacing = Tokens.Space.Tight };

    /// <summary>Builds the list.</summary>
    /// <param name="store">Transcript history.</param>
    /// <param name="openHistory">Shows the History page.</param>
    public RecentDictations(TranscriptStore store, Action openHistory)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;

        var all = Panels.QuietButton("See all");
        all.Click += (_, _) => openHistory();

        Content = new StackPanel
        {
            Spacing = Tokens.Space.Tight,
            Children =
            {
                new DockPanel
                {
                    LastChildFill = true,
                    Margin = new Thickness(Tokens.Space.Tight, 0, 0, Tokens.Space.Tight),
                    Children =
                    {
                        Panels.Docked(all, Dock.Right),
                        new Caption { Text = "Recent", IsHeader = true, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                _list,
            },
        };

        _store.Changed += (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess()) Refresh();
            else Dispatcher.UIThread.Post(Refresh);
        };
        Refresh();
    }

    private void Refresh()
    {
        _list.Children.Clear();

        var records = _store.Records.Take(Count).ToList();
        if (records.Count == 0)
        {
            _list.Children.Add(Panels.Card(Panels.Note(
                "Nothing yet. Hold the push-to-talk key, say something, and let go.")));
            return;
        }

        foreach (var record in records)
        {
            _list.Children.Add(Panels.Card(new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    Panels.Docked(new TextBlock
                    {
                        Text = record.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture),
                        FontFamily = Tokens.Fonts.Ui,
                        FontSize = Tokens.Fonts.Label,
                        Margin = new Thickness(Tokens.Space.Roomy, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                        [!TextBlock.ForegroundProperty] = Tokens.Live.InkMuted,
                    }, Dock.Right),
                    new TextBlock
                    {
                        Text = record.Text,
                        FontFamily = Tokens.Fonts.Ui,
                        FontSize = Tokens.Fonts.Body,
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2,
                        TextTrimming = TextTrimming.WordEllipsis,
                        [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
                    },
                },
            }));
        }
    }
}
