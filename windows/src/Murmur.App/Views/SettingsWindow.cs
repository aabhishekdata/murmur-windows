using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Speech;

namespace Murmur.App.Views;

/// <summary>
/// Preferences: the hotkey, the model, the bar and what happens with finished text.
/// </summary>
/// <remarks>
/// A view rather than a window, so the same controls serve the hub's Settings section and the
/// standalone dialog the tray menu opens. Two hand-maintained copies of a settings screen
/// diverge; this cannot.
/// </remarks>
public sealed class SettingsView : UserControl
{
    /// <summary>
    /// The keys offered, in recommendation order.
    /// </summary>
    /// <remarks>
    /// Right Alt is included but listed last and carries a warning: on German, Polish, UK,
    /// Nordic and most Latin-American layouts it is AltGr, and binding push-to-talk there
    /// breaks typing <c>@</c>, <c>€</c>, <c>\</c> and <c>|</c>.
    /// </remarks>
    private static readonly (int Key, string Label, string? Warning)[] Keys =
    [
        (0xA3, "Right Ctrl", null),
        (0xA1, "Right Shift", null),
        (0x14, "Caps Lock", null),
        (0x7C, "F13", null),
        (0xA5, "Right Alt", "Right Alt is AltGr on many European layouts — binding it here "
                          + "will interfere with typing @, €, \\ and |."),
    ];

    private static readonly (ScreenEdge Edge, string Label)[] Edges =
    [
        (ScreenEdge.Bottom, "Bottom"),
        (ScreenEdge.Left, "Left"),
        (ScreenEdge.Right, "Right"),
    ];

    private readonly AppSettings _settings;
    private readonly IAudioDeviceCatalog? _microphones;
    private readonly StackPanel _keyRow;
    private readonly StackPanel _edgeRow;
    private readonly StackPanel _micList;
    private readonly TextBlock _keyWarning;

    /// <summary>Builds the view over <paramref name="settings"/>.</summary>
    /// <param name="settings">User preferences.</param>
    /// <param name="microphones">
    /// The capture devices to offer, or null off Windows — in which case the picker shows the
    /// system default alone rather than an empty box.
    /// </param>
    public SettingsView(AppSettings settings, IAudioDeviceCatalog? microphones = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _microphones = microphones;
        _micList = new StackPanel { Spacing = Tokens.Space.Tight };

        _keyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.Snug };
        _edgeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.Snug };

        _keyWarning = new TextBlock
        {
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Label,
            Foreground = Tokens.Brushes.Notice,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        foreach (var (key, label, warning) in Keys)
        {
            var button = new SoftButton { Content = label, Tone = ButtonTone.Outline };
            button.Click += (_, _) => SelectKey(key, warning);
            _keyRow.Children.Add(button);
        }

        foreach (var (edge, label) in Edges)
        {
            var button = new SoftButton { Content = label, Tone = ButtonTone.Outline };
            button.Click += (_, _) => SelectEdge(edge);
            _edgeRow.Children.Add(button);
        }

        Content = new ScrollViewer { Content = BuildContent() };

        SelectKey(_settings.Data.PushToTalkKey, WarningFor(_settings.Data.PushToTalkKey));
        HighlightEdge(_settings.Data.FlowBarEdge);
        RefreshMicrophones();
    }

    private static string? WarningFor(int key) => Keys.FirstOrDefault(k => k.Key == key).Warning;

    private StackPanel BuildContent() => new()
    {
        Margin = new Thickness(Tokens.Space.Wide, 0, Tokens.Space.Wide, Tokens.Space.Wide),
        Spacing = Tokens.Space.Roomy,
        Children =
        {
            Panels.Section("Push to talk", new StackPanel
            {
                Spacing = Tokens.Space.Base,
                Children =
                {
                    _keyRow,
                    _keyWarning,
                    Panels.Note("Hold this key anywhere to dictate. The key is passed through to "
                              + "the focused app rather than swallowed, so it never gets stuck down."),
                },
            }),

            Panels.Section("Microphone", new StackPanel
            {
                Spacing = Tokens.Space.Base,
                Children =
                {
                    _micList,
                    Panels.Note("Choosing a microphone here overrides both of Windows' defaults. "
                              + "If the one you picked is unplugged, Murmur falls back to the "
                              + "system default rather than failing to record."),
                },
            }),

            Panels.Section("Model", BuildModelSection()),

            Panels.Section("Floating bar", new StackPanel
            {
                Spacing = Tokens.Space.Base,
                Children =
                {
                    _edgeRow,
                    Panels.Note("The bar can also be dragged anywhere and will settle on the "
                              + "nearest edge. Its position is remembered between launches."),
                    Toggle("Show the floating bar", _settings.Data.ShowFlowBar,
                        v => Save(_settings.Data with { ShowFlowBar = v })),
                },
            }),

            Panels.Section("Clean up what I say", new StackPanel
            {
                Spacing = Tokens.Space.Snug,
                Children =
                {
                    Toggle("Remove \"um\", \"uh\" and other filler words",
                        _settings.Data.RemoveFillers,
                        v => Save(_settings.Data with { RemoveFillers = v })),
                    Toggle("Honour spoken corrections like \"wait no\"",
                        _settings.Data.ApplySelfCorrections,
                        v => Save(_settings.Data with { ApplySelfCorrections = v })),
                    Toggle("Turn spoken lists into real lists",
                        _settings.Data.FormatLists,
                        v => Save(_settings.Data with { FormatLists = v })),
                    Toggle("Expand snippet triggers",
                        _settings.Data.ExpandSnippets,
                        v => Save(_settings.Data with { ExpandSnippets = v })),
                    Toggle("Ending with \"press enter\" submits instead of typing it",
                        _settings.Data.EnterCommand,
                        v => Save(_settings.Data with { EnterCommand = v })),
                    Panels.Note("Every one of these is pattern matching, not a language model — "
                              + "nothing you say leaves the machine, and the same words always "
                              + "produce the same text. Turn any of them off if it guesses "
                              + "wrong about the way you speak."),
                },
            }),

            Panels.Section("Text", new StackPanel
            {
                Spacing = Tokens.Space.Snug,
                Children =
                {
                    Toggle("Type transcripts into the focused app", _settings.Data.InjectText,
                        v => Save(_settings.Data with { InjectText = v })),
                    Toggle("Keep a transcript history", _settings.Data.KeepHistory,
                        v => Save(_settings.Data with { KeepHistory = v })),
                },
            }),
        },
    };

    private static StackPanel BuildModelSection()
    {
        var located = ParakeetTranscriber.Locate();
        var found = located is not null;

        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children =
            {
                new StatusDot
                {
                    IsLit = true,
                    DotColor = found ? Tokens.Colors.Accent : Tokens.Colors.Notice,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = found ? "Parakeet ready" : "Model not installed",
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Body,
                    FontWeight = FontWeight.Medium,
                    Foreground = Tokens.Brushes.Ink,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        var detail = found
            // Showing the resolved path matters: "model not found" is unactionable without
            // knowing which directory was actually checked.
            ? Panels.Note($"Loaded from {located}")
            : Panels.Note("Windows has no built-in speech engine equivalent to Apple's, so Murmur "
                        + "cannot transcribe until the Parakeet model is downloaded (~661 MB). "
                        + "See docs/PARAKEET-WINDOWS.md. Expected in:\n"
                        + string.Join("\n", ParakeetTranscriber.DefaultSearchPaths()));

        return new StackPanel { Spacing = Tokens.Space.Snug, Children = { status, detail } };
    }

    private void SelectKey(int key, string? warning)
    {
        for (var i = 0; i < Keys.Length; i++)
        {
            ((SoftButton)_keyRow.Children[i]).IsSelected = Keys[i].Key == key;
        }

        _keyWarning.Text = warning ?? string.Empty;
        _keyWarning.IsVisible = warning is not null;

        if (_settings.Data.PushToTalkKey != key) Save(_settings.Data with { PushToTalkKey = key });
    }

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
        _micList.Children.Clear();

        var chosen = _settings.Data.MicrophoneId;
        var devices = _microphones?.List() ?? [];

        _micList.Children.Add(MicrophoneRow(
            null,
            "System default",
            devices.FirstOrDefault(d => d.IsDefault).Name,
            chosen is null));

        foreach (var device in devices)
        {
            _micList.Children.Add(MicrophoneRow(
                device.Id,
                device.Name,
                device.IsDefault ? "Currently the Windows default" : null,
                string.Equals(chosen, device.Id, StringComparison.Ordinal)));
        }

        if (devices.Count == 0)
        {
            _micList.Children.Add(Panels.Note(
                "No microphones were found. On Windows this usually means the app has not been "
                + "granted microphone access yet — Settings → Privacy → Microphone."));
        }
    }

    private Border MicrophoneRow(string? id, string name, string? detail, bool selected)
    {
        var label = new StackPanel
        {
            Spacing = Tokens.Space.Hair,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = name,
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Body,
                    FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = selected ? Tokens.Brushes.Accent : Tokens.Brushes.Ink,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };

        if (!string.IsNullOrEmpty(detail)) label.Children.Add(new Caption { Text = detail });

        var row = new Border
        {
            Background = selected ? Tokens.Brushes.AccentSoft : Tokens.Brushes.Surface,
            CornerRadius = new CornerRadius(Tokens.Radius.Control),
            BorderBrush = selected ? new SolidColorBrush(Tokens.Colors.Accent, 0.4) : Tokens.Brushes.Line,
            BorderThickness = new Thickness(Tokens.Border.Hairline),
            Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    Panels.Docked(new StatusDot
                    {
                        IsLit = selected,
                        DotColor = Tokens.Colors.Accent,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, Tokens.Space.Base, 0),
                    }, Dock.Left),
                    label,
                },
            },
        };

        row.PointerPressed += (_, _) => SelectMicrophone(id);
        return row;
    }

    private void SelectMicrophone(string? id)
    {
        if (string.Equals(_settings.Data.MicrophoneId, id, StringComparison.Ordinal)) return;

        Save(_settings.Data with { MicrophoneId = id });
        RefreshMicrophones();
    }

    private void SelectEdge(ScreenEdge edge)
    {
        HighlightEdge(edge);
        if (_settings.Data.FlowBarEdge != edge) Save(_settings.Data with { FlowBarEdge = edge });
    }

    private void HighlightEdge(ScreenEdge edge)
    {
        for (var i = 0; i < Edges.Length; i++)
        {
            ((SoftButton)_edgeRow.Children[i]).IsSelected = Edges[i].Edge == edge;
        }
    }

    private void Save(SettingsData data) => _settings.Update(data);

    private static CheckBox Toggle(string label, bool value, Action<bool> onChange)
    {
        var box = new CheckBox
        {
            IsChecked = value,
            Content = new TextBlock
            {
                Text = label,
                FontFamily = Tokens.Fonts.Ui,
                FontSize = Tokens.Fonts.Body,
                Foreground = Tokens.Brushes.Ink,
            },
        };

        box.IsCheckedChanged += (_, _) => onChange(box.IsChecked ?? false);
        return box;
    }
}

/// <summary>
/// Settings as a standalone dialog, for the tray menu.
/// </summary>
/// <remarks>
/// A thin shell around <see cref="SettingsView"/>. The tray has to be able to reach settings
/// without the hub being open — that is the point of a tray-resident app — but the controls
/// themselves are shared.
/// </remarks>
public sealed class SettingsWindow : Window
{
    /// <summary>Builds the dialog.</summary>
    /// <param name="settings">User preferences.</param>
    /// <param name="microphones">The capture devices to offer, or null off Windows.</param>
    public SettingsWindow(AppSettings settings, IAudioDeviceCatalog? microphones = null)
    {
        Title = "Murmur Settings";
        Width = 560;
        Height = 640;
        CanResize = false;
        Background = Tokens.Brushes.Canvas;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Panels.Docked(new StackPanel
                {
                    Margin = new Thickness(Tokens.Space.Wide, Tokens.Space.Wide, Tokens.Space.Wide, Tokens.Space.Roomy),
                    Children = { Panels.Title("Settings") },
                }, Dock.Top),
                new SettingsView(settings, microphones),
            },
        };
    }
}
