using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Speech;

namespace Murmur.App.Views;

/// <summary>
/// Preferences, as Windows 11 Settings lays them out: groups of expander cards.
/// </summary>
/// <remarks>
/// <para>
/// Six groups — General, Transcription, Clean-up, Prompts &amp; shortcuts, Privacy &amp; data,
/// About — each a header over cards with an icon, a title, a one-line description and the
/// control on the right. Choices are toggle switches, combo boxes and segmented controls; there
/// are no check boxes and no rows of buttons standing in for a choice.
/// </para>
/// <para>
/// A view rather than a window, so the same controls serve the hub's Settings page and the
/// standalone window the tray menu opens. Two hand-maintained copies of a settings screen
/// diverge; this cannot.
/// </para>
/// </remarks>
public sealed class SettingsView : UserControl
{
    private readonly AppSettings _settings;
    private readonly IAudioDeviceCatalog? _microphones;
    private readonly CloudServices? _cloud;
    private readonly ModelLocator _models;
    private readonly Border _keyWarning;
    private readonly ComboBox _microphone;
    private readonly FASettingsExpander _microphoneCard;

    private const string MicrophoneDescription =
        "Overrides Windows' default. If the one you pick is unplugged, Murmur falls back to the system default.";
    private readonly TextBlock _noMicrophones;
    private readonly StackPanel _openPrompts = new() { Orientation = Orientation.Horizontal };
    private readonly FASettingsExpander _privacyCard;
    private readonly PasteLastTranscript? _pasteLast;
    private readonly SnippetHotkeyBinder? _promptHotkeys;
    private readonly TextBlock _pasteLastStatus;
    private readonly TextBlock _languageWarning;
    private List<(string? Id, string Label)> _microphoneOptions = [];
    private bool _refreshingMicrophones;

    /// <summary>Builds the view over <paramref name="settings"/>.</summary>
    /// <param name="settings">User preferences.</param>
    /// <param name="microphones">
    /// The capture devices to offer, or null off Windows — in which case the picker shows the
    /// system default alone rather than an empty box.
    /// </param>
    /// <param name="cloud">The Azure key and connection test, or null where there are none.</param>
    /// <param name="models">Where the offline model is looked for, or null to make a locator.</param>
    /// <param name="pasteLast">The paste-last shortcut, to show whether it registered; null where there is none.</param>
    /// <param name="promptHotkeys">
    /// The saved prompts' shortcuts, given back with the paste-last one while a new one is
    /// recorded; null where there are none.
    /// </param>
    public SettingsView(
        AppSettings settings,
        IAudioDeviceCatalog? microphones = null,
        CloudServices? cloud = null,
        ModelLocator? models = null,
        PasteLastTranscript? pasteLast = null,
        SnippetHotkeyBinder? promptHotkeys = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _microphones = microphones;
        _cloud = cloud;
        _pasteLast = pasteLast;
        _promptHotkeys = promptHotkeys;

        _pasteLastStatus = Panels.Note(string.Empty);
        _pasteLastStatus.TextAlignment = TextAlignment.Right;
        _pasteLastStatus.HorizontalAlignment = HorizontalAlignment.Right;
        _languageWarning = Footnote(string.Empty);

        // Through the locator, so a model folder chosen in settings is reported as found — the
        // plain search ignored it and said "not installed" about a model that loaded.
        _models = models ?? new ModelLocator(() => _settings.Data.ModelDirectory);

        _keyWarning = Panels.Notice(Choices.AltGrWarning);
        _keyWarning.IsVisible = Choices.WarningFor(_settings.Data.PushToTalkKey) is not null;

        _microphone = new ComboBox { MinWidth = 240, MaxWidth = 320 };
        _microphoneCard = Panels.Setting(Icons.Mic, "Microphone", MicrophoneDescription, _microphone);
        _microphone.SelectionChanged += (_, _) => OnMicrophoneChosen();
        _noMicrophones = Footnote(
            "No microphones were found. On Windows this usually means the app has not been granted "
            + "microphone access yet — Settings → Privacy & security → Microphone.");

        _privacyCard = Panels.Setting(Icons.Privacy, "Where your voice goes", null, null);

        Content = new StackPanel
        {
            Spacing = Tokens.Space.Section,
            Children =
            {
                BuildGeneral(),
                BuildTranscription(),
                BuildCleanUp(),
                BuildPrompts(),
                BuildPrivacy(),
                BuildAbout(),
            },
        };

        RefreshMicrophones();
        RefreshAudioDestination();
        RefreshPasteLastStatus();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Subscribed only while on screen: the tray builds a new settings window each time, and a
    /// handler left on the long-lived settings object would keep every one of them alive.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _settings.Changed -= OnSettingsChanged;
        _settings.Changed += OnSettingsChanged;
        RefreshAudioDestination();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _settings.Changed -= OnSettingsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RefreshAudioDestination();
            RefreshLanguageWarning();
        });

    /// <summary>
    /// Shows a "Manage prompts and snippets" button that calls this. Set by the hub; the
    /// standalone window has no page to go to and leaves it null.
    /// </summary>
    public Action? OpenPrompts
    {
        get => _openPromptsAction;
        set
        {
            _openPromptsAction = value;
            _openPrompts.Children.Clear();
            if (value is null) return;

            var open = Panels.OutlineButton("Manage");
            open.Click += (_, _) => _openPromptsAction?.Invoke();
            _openPrompts.Children.Add(open);
        }
    }

    private Action? _openPromptsAction;

    /// <summary>The six groups, in order. Exposed for headless tests and previews.</summary>
    internal IReadOnlyList<Control> Groups => ((StackPanel)Content!).Children.ToList();

    /// <summary>The microphones offered, in order. Exposed for headless tests.</summary>
    internal IReadOnlyList<string> MicrophoneChoices => _microphoneOptions.Select(o => o.Label).ToList();

    // ---- Groups ----

    private StackPanel BuildGeneral()
    {
        var key = Panels.Choice(Choices.Keys, _settings.Data.PushToTalkKey, SelectKey);
        key.MinWidth = 160;

        var edge = Panels.Choice(Choices.Edges, _settings.Data.FlowBarEdge, edge =>
        {
            if (_settings.Data.FlowBarEdge != edge) Save(_settings.Data with { FlowBarEdge = edge });
        });

        // Every combination Murmur holds is given back while recording, not just this one: a
        // prompt's would otherwise be swallowed by Windows and paste the prompt instead.
        var pasteLast = new ShortcutRecorder(
            _settings.Data.PasteLastHotkey,
            _pasteLast is null ? null : () => ShortcutSuspension.All(_promptHotkeys, _pasteLast))
        {
            Width = 240,
        };
        pasteLast.ChordChanged += (_, _) =>
        {
            if (_settings.Data.PasteLastHotkey != pasteLast.Chord) Save(_settings.Data with { PasteLastHotkey = pasteLast.Chord });
            RefreshPasteLastStatus();
        };
        PasteLastRecorder = pasteLast;

        return Panels.Group("General",
            Panels.Setting(Icons.Keyboard, "Push-to-talk key",
                "Hold it anywhere to dictate. It is passed through to the app you're in, never swallowed, so it can't get stuck down.",
                key),
            _keyWarning,
            Panels.Setting(Icons.HandWave, "Hands-free",
                "Double-tap the push-to-talk key to keep listening without holding it; tap it once more to stop. "
                + "Esc cancels any dictation, held or hands-free, and nothing is typed.",
                Panels.Toggle(_settings.Data.HandsFreeDoubleTap, v => Save(_settings.Data with { HandsFreeDoubleTap = v }))),
            Panels.Setting(Icons.Prompts, "Paste last transcript",
                "Pastes what you last dictated again, wherever the cursor is. The tray and the bar's menu copy it instead.",
                new StackPanel
                {
                    Spacing = Tokens.Space.Tight,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { pasteLast, _pasteLastStatus },
                }),
            _microphoneCard,
            _noMicrophones,
            Panels.Expander(Icons.Laptop, "Floating bar",
                "Shows when Murmur is listening. Drag it anywhere — it settles on the nearest edge and remembers where.",
                Panels.Toggle(_settings.Data.ShowFlowBar, v => Save(_settings.Data with { ShowFlowBar = v })),
                Panels.Item("Position", null, edge)),
            Panels.Setting(Icons.CursorClick, "Type into the app you're using",
                "When this is off, transcripts are only kept in History.",
                Panels.Toggle(_settings.Data.InjectText, v => Save(_settings.Data with { InjectText = v }))));
    }

    private StackPanel BuildTranscription()
    {
        var language = Panels.Choice(LanguageModes.Choices, _settings.Data.Language, mode =>
        {
            if (_settings.Data.Language != mode) Save(_settings.Data with { Language = mode });
            RefreshLanguageWarning();
        });
        language.MinWidth = 200;

        var group = Panels.Group("Transcription",
            new TranscriptionEngineCard(_settings, _cloud),
            Panels.Setting(Icons.Cloud, "Language",
                "What you speak. Hindi and Hinglish are understood by the Azure engines only; Parakeet is English-only.",
                language),
            _languageWarning,
            BuildModelCard());

        RefreshLanguageWarning();
        return group;
    }

    private FASettingsExpander BuildModelCard()
    {
        var located = _models.Directory;
        var found = located is not null;

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new StatusDot
                {
                    IsLit = true,
                    Token = found ? ColorToken.Accent : ColorToken.Notice,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = found ? "Parakeet ready" : "Not installed",
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Body,
                    VerticalAlignment = VerticalAlignment.Center,
                    [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
                },
            },
        };

        // Showing the resolved path matters: "model not found" is unactionable without knowing
        // which directory was actually checked.
        var detail = found
            ? Panels.Note($"Loaded from {located}")
            : Panels.Note("Run get-model.ps1 (beside Murmur.exe) once to download it — about 661 MB. "
                        + "Murmur looks in:\n" + string.Join("\n", ParakeetTranscriber.DefaultSearchPaths()));
        detail.TextWrapping = TextWrapping.Wrap;

        return Panels.Expander(Icons.ArrowDownload, "Offline model",
            "Parakeet, the engine that runs on this PC. It is the default and the fallback.",
            status,
            Panels.Item(detail));
    }

    private StackPanel BuildCleanUp()
    {
        var group = Panels.Group("Clean-up",
            Panels.Setting(Icons.CleanUp, "Remove filler words",
                "Drops \"um\", \"uh\" and the like from what you say.",
                Panels.Toggle(_settings.Data.RemoveFillers, v => Save(_settings.Data with { RemoveFillers = v }))),
            Panels.Setting(Icons.CleanUp, "Honour spoken corrections",
                "\"Tuesday, wait no, Friday\" is typed as \"Friday\".",
                Panels.Toggle(_settings.Data.ApplySelfCorrections, v => Save(_settings.Data with { ApplySelfCorrections = v }))),
            Panels.Setting(Icons.CleanUp, "Format spoken lists",
                "\"One, milk. Two, eggs.\" becomes a numbered list.",
                Panels.Toggle(_settings.Data.FormatLists, v => Save(_settings.Data with { FormatLists = v }))));

        group.Children.Add(Footnote(
            "Every one of these is pattern matching, not a language model — it runs on this PC whichever "
            + "engine heard you, and the same words always produce the same text. Turn any of them off if it "
            + "guesses wrong about the way you speak."));
        return group;
    }

    private StackPanel BuildPrompts() => Panels.Group("Prompts & shortcuts",
        Panels.Setting(Icons.Prompts, "Expand prompts and snippets",
            "Say a saved prompt's name to paste all of it, or a snippet's trigger to drop it into a sentence.",
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.Space.Base,
                Children =
                {
                    _openPrompts,
                    Panels.Toggle(_settings.Data.ExpandSnippets, v => Save(_settings.Data with { ExpandSnippets = v })),
                },
            }),
        Panels.Setting(Icons.Keyboard, "Ending with \"press enter\" submits",
            "Say it last and Murmur presses Enter instead of typing the words — for chat boxes.",
            Panels.Toggle(_settings.Data.EnterCommand, v => Save(_settings.Data with { EnterCommand = v }))));

    private StackPanel BuildPrivacy() => Panels.Group("Privacy & data",
        _privacyCard,
        Panels.Setting(Icons.History, "Keep a transcript history",
            "Kept on this PC only, in %LocalAppData%\\Murmur. Turn it off and nothing you say is written down.",
            Panels.Toggle(_settings.Data.KeepHistory, v => Save(_settings.Data with { KeepHistory = v }))),
        Panels.Setting(Icons.History, "Keep recent audio for 24 hours",
            "So \"Retry with the other engine\" still works after a restart. Recordings are written to "
            + "%LocalAppData%\\Murmur\\recent-audio and deleted after a day, when you delete them from History, "
            + "or as soon as you turn this off. When it's off, audio is never written to disk.",
            Panels.Toggle(_settings.Data.KeepRecentAudioOnDisk, v => Save(_settings.Data with { KeepRecentAudioOnDisk = v }))),
        Panels.Setting(Icons.Key, "Your API key",
            "Encrypted for your Windows account with DPAPI and kept in its own file — never in settings.json, "
            + "never in a log. Only its last four characters are ever shown.",
            null));

    private static StackPanel BuildAbout()
    {
        var version = typeof(SettingsView).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(SettingsView).Assembly.GetName().Version?.ToString()
            ?? "dev";

        // The build adds "+<commit>"; the first part is the version a person can compare.
        version = version.Split('+', 2)[0];

        return Panels.Group("About",
            Panels.Expander(Icons.Info, "Murmur for Windows", $"Version {version}", null,
                Panels.Item("Offline speech", "NVIDIA Parakeet TDT 0.6B v2, run by sherpa-onnx (Apache 2.0).", null),
                Panels.Item("Interface", "Avalonia and FluentAvalonia (MIT).", null),
                Panels.Item("Icons", "Fluent System Icons, © Microsoft Corporation (MIT).", null),
                Panels.Item("Licences", "THIRD-PARTY-NOTICES.md, beside the app, has the full texts.", null)));
    }

    private static TextBlock Footnote(string text)
    {
        var note = Panels.Note(text);
        note.Margin = new Thickness(Tokens.Space.Tight, Tokens.Space.Snug, Tokens.Space.Tight, 0);
        return note;
    }

    // ---- Behaviour ----

    /// <summary>The paste-last shortcut recorder. Exposed for headless tests.</summary>
    internal ShortcutRecorder? PasteLastRecorder { get; private set; }

    /// <summary>What the paste-last status line says. Exposed for headless tests.</summary>
    internal string PasteLastStatus => _pasteLastStatus.Text ?? string.Empty;

    /// <summary>The warning under the language choice, or empty. Exposed for headless tests.</summary>
    internal string LanguageWarning => _languageWarning.IsVisible ? _languageWarning.Text ?? string.Empty : string.Empty;

    private void RefreshPasteLastStatus()
    {
        var status = _pasteLast?.Status ?? SnippetHotkeyStatus.None;
        _pasteLastStatus.Text = SnippetHotkeyBinder.Describe(status, _pasteLast?.Chord) ?? string.Empty;
        _pasteLastStatus.IsVisible = _pasteLastStatus.Text.Length > 0;
    }

    private void RefreshLanguageWarning()
    {
        var data = _settings.Data;
        _languageWarning.Text = LanguageModes.Warning(data.Language, data.Engine) ?? string.Empty;
        _languageWarning.IsVisible = _languageWarning.Text.Length > 0;
        _languageWarning.Bind(TextBlock.ForegroundProperty, Tokens.Live.Notice);
    }

    private void SelectKey(int key)
    {
        _keyWarning.IsVisible = Choices.WarningFor(key) is not null;
        if (_settings.Data.PushToTalkKey != key) Save(_settings.Data with { PushToTalkKey = key });
    }

    private void RefreshAudioDestination()
    {
        var data = _settings.Data;
        _privacyCard.Description = data.Engine switch
        {
            EngineMode.Azure =>
                $"Audio is sent to your own Azure OpenAI resource ({Host(data.AzureEndpoint)}) with your key, "
                + "and nowhere else. The text clean-up still runs on this PC.",
            EngineMode.AzureWithParakeetFallback =>
                $"Audio is sent to your own Azure OpenAI resource ({Host(data.AzureEndpoint)}). When Azure can't "
                + "be reached, Parakeet on this PC answers instead. Nothing goes anywhere else.",
            _ => "Nothing leaves this PC. Parakeet listens and transcribes locally, and the clean-up is local too. "
               + "There is no telemetry.",
        };
    }

    /// <summary>The host audio is actually sent to, read the way the transcriber reads it.</summary>
    /// <remarks>
    /// The stored text is what the user pasted — often a bare host, which a plain URL parse
    /// refuses. Normalized exactly as the request is built, so this names the real destination.
    /// </remarks>
    internal static string Host(string? endpoint) =>
        Murmur.Cloud.AzureEndpoint.TryNormalize(endpoint, out var uri, out _) ? uri.Host : "not set up yet";

    /// <summary>
    /// Rebuilds the microphone list.
    /// </summary>
    /// <remarks>
    /// "System default" is always offered and always first: it is the only entry guaranteed to
    /// keep working when a USB microphone moves to a different port, and it is what a user who
    /// has never thought about this should be on.
    /// </remarks>
    private void RefreshMicrophones()
    {
        var chosen = _settings.Data.MicrophoneId;
        var devices = _microphones?.List() ?? [];
        var fallback = devices.FirstOrDefault(d => d.IsDefault).Name;

        _microphoneOptions =
        [
            (null, "System default"),
            .. devices.Select(d => ((string?)d.Id, d.Name)),
        ];

        // Which device "System default" is right now, where the choice is made — the combo box
        // itself only has room for the name.
        _microphoneCard.Description = string.IsNullOrEmpty(fallback)
            ? MicrophoneDescription
            : $"{MicrophoneDescription} The system default is {fallback}.";

        _refreshingMicrophones = true;
        try
        {
            _microphone.ItemsSource = _microphoneOptions.Select(o => o.Label).ToList();
            var index = _microphoneOptions.FindIndex(o => string.Equals(o.Id, chosen, StringComparison.Ordinal));
            _microphone.SelectedIndex = Math.Max(0, index);
        }
        finally
        {
            _refreshingMicrophones = false;
        }

        _noMicrophones.IsVisible = devices.Count == 0;
    }

    private void OnMicrophoneChosen()
    {
        if (_refreshingMicrophones || _microphone.SelectedIndex < 0) return;

        var id = _microphoneOptions[_microphone.SelectedIndex].Id;
        if (string.Equals(_settings.Data.MicrophoneId, id, StringComparison.Ordinal)) return;

        Save(_settings.Data with { MicrophoneId = id });
    }

    private void Save(SettingsData data) => _settings.Update(data);
}

/// <summary>
/// Settings as a standalone window, for the tray menu.
/// </summary>
/// <remarks>
/// A thin host of <see cref="SettingsView"/> in the same Windows 11 frame as the hub. The tray has
/// to be able to reach settings without the hub being open — that is the point of a
/// tray-resident app — but the controls themselves are shared.
/// </remarks>
public sealed class SettingsWindow : Window
{
    /// <summary>Builds the window.</summary>
    /// <param name="settings">User preferences.</param>
    /// <param name="microphones">The capture devices to offer, or null off Windows.</param>
    /// <param name="cloud">The Azure key and connection test, or null where there are none.</param>
    /// <param name="models">Where the offline model is looked for, or null.</param>
    /// <param name="pasteLast">The paste-last shortcut, or null.</param>
    /// <param name="promptHotkeys">The saved prompts' shortcuts, or null.</param>
    public SettingsWindow(
        AppSettings settings,
        IAudioDeviceCatalog? microphones = null,
        CloudServices? cloud = null,
        ModelLocator? models = null,
        PasteLastTranscript? pasteLast = null,
        SnippetHotkeyBinder? promptHotkeys = null)
    {
        Title = "Murmur Settings";
        Width = 760;
        Height = 760;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowChrome.Install(this);

        View = new SettingsView(settings, microphones, cloud, models, pasteLast, promptHotkeys);

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Panels.Docked(WindowChrome.TitleBar("Murmur Settings"), Dock.Top),
                Panels.Docked(new Border
                {
                    Padding = new Thickness(Tokens.Space.Section, Tokens.Space.Base, Tokens.Space.Section, Tokens.Space.Roomy),
                    Child = Panels.Title("Settings"),
                }, Dock.Top),
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = new Border
                    {
                        Padding = new Thickness(Tokens.Space.Section, 0, Tokens.Space.Section, Tokens.Space.Section),
                        Child = View,
                    },
                },
            },
        };
    }

    /// <summary>The page this window hosts. Exposed for headless tests.</summary>
    internal SettingsView View { get; }
}
