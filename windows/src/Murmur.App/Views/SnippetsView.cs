using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Murmur.Abstractions;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>
/// Prompts and snippets: a spoken phrase (or a key combination) in, a stored block of text out.
/// </summary>
/// <remarks>
/// <para>
/// Built to the same shape as <see cref="DictionaryView"/> on purpose — the two features are
/// structurally identical from the user's side (stored entries, a search, a row per entry, an
/// editor dialog), and making them look and behave differently would be inventing a second way
/// to do the same thing.
/// </para>
/// <para>
/// One store holds both kinds. A <i>prompt</i> fires only when its trigger is the whole
/// dictation and is pasted verbatim; a <i>snippet</i> expands wherever its trigger is said. The
/// row says which, along with any shortcut and whether that shortcut could be registered.
/// </para>
/// </remarks>
public sealed class SnippetsView : UserControl
{
    private readonly SnippetStore _store;
    private readonly SnippetHotkeyBinder? _hotkeys;
    private readonly PasteLastTranscript? _pasteLast;
    private readonly TextBox _search;
    private readonly Debouncer _searchDebounce;
    private readonly StackPanel _list;
    private readonly Caption _count;

    /// <summary>Builds the view over <paramref name="store"/>.</summary>
    /// <param name="store">The snippet store.</param>
    /// <param name="hotkeys">Registers prompt shortcuts and reports conflicts; null where there is none.</param>
    /// <param name="pasteLast">
    /// The paste-last shortcut, given back with the prompts' while one is recorded; null where there is none.
    /// </param>
    public SnippetsView(SnippetStore store, SnippetHotkeyBinder? hotkeys = null, PasteLastTranscript? pasteLast = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _hotkeys = hotkeys;
        _pasteLast = pasteLast;

        _search = Panels.SearchBox("Search prompts and snippets");
        _searchDebounce = new Debouncer(Tokens.Motion.SearchDebounce, Refresh);
        _search.TextChanged += (_, _) => _searchDebounce.Trigger();

        var add = Panels.PrimaryButton("New prompt");
        add.Click += (_, _) => ShowEditor(null);

        _list = new StackPanel
        {
            Spacing = Tokens.Space.Snug,
            Margin = new Thickness(Tokens.Space.Section, Tokens.Space.Tight, Tokens.Space.Section, Tokens.Space.Roomy),
        };

        _count = new Caption();

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Panels.Docked(Panels.SearchRow(_search, add), Dock.Top),
                Panels.Docked(Panels.Footer(_count, Hint()), Dock.Bottom),
                new ScrollViewer { Content = _list },
            },
        };

        _store.Changed += (_, _) => Refresh();

        // Statuses are recomputed on whichever thread changed the store; the list is rebuilt on
        // the UI thread either way.
        if (_hotkeys is not null)
        {
            _hotkeys.Changed += (_, _) =>
            {
                if (Dispatcher.UIThread.CheckAccess()) Refresh();
                else Dispatcher.UIThread.Post(Refresh);
            };
        }

        Refresh();
    }

    private static Caption Hint() => new()
    {
        Text = "Say a prompt's trigger on its own, or a snippet's anywhere",
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void Refresh()
    {
        var entries = _store.Search(_search.Text ?? string.Empty);

        _list.Children.Clear();
        _count.Text = CountText(_store.Entries);

        if (entries.Count == 0)
        {
            _list.Children.Add(Panels.EmptyState(
                _store.Entries.Count == 0 ? "No prompts or snippets yet" : "No matches",
                _store.Entries.Count == 0
                    ? "Save a prompt you use every day and say its name to paste all of it, or "
                    + "give it a shortcut. Snippets put an address or a signature mid-sentence."
                    : "Try a different search."));
            return;
        }

        foreach (var entry in entries) _list.Children.Add(BuildRow(entry));
    }

    /// <summary>"2 prompts · 1 snippet", leaving out a kind there are none of.</summary>
    internal static string CountText(IReadOnlyList<SnippetEntry> entries)
    {
        var prompts = entries.Count(e => e.Match == SnippetMatch.WholeUtterance);
        var snippets = entries.Count - prompts;

        var parts = new List<string>(2);
        if (prompts > 0) parts.Add(prompts == 1 ? "1 prompt" : $"{prompts} prompts");
        if (snippets > 0) parts.Add(snippets == 1 ? "1 snippet" : $"{snippets} snippets");

        return parts.Count == 0 ? "Nothing saved yet" : string.Join(" · ", parts);
    }

    private Border BuildRow(SnippetEntry entry)
    {
        var edit = Panels.QuietButton("Edit");
        edit.Click += (_, _) => ShowEditor(entry);

        var toggle = Panels.QuietButton(entry.IsEnabled ? "Disable" : "Enable");
        toggle.Click += (_, _) => _store.Update(entry with { IsEnabled = !entry.IsEnabled });

        var delete = Panels.QuietButton("Delete");
        delete.Tone = ButtonTone.Danger;
        delete.Click += (_, _) => _store.Remove(entry.Id);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Tight,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0,
            Children = { edit, toggle, delete },
        };

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Panels.Chip(entry.Match == SnippetMatch.WholeUtterance ? "Say only" : "Say", accent: true),
                new TextBlock
                {
                    Text = entry.Trigger,
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Body,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
                },
                new Caption
                {
                    Text = entry.Text.Length == 1 ? "1 character" : $"{entry.Text.Length:N0} characters",
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        if (entry.Hotkey is { } chord)
        {
            heading.Children.Insert(2, Panels.Chip(HotkeyChords.Format(chord)));
        }

        var body = new StackPanel
        {
            Spacing = Tokens.Space.Snug,
            Children =
            {
                new Grid { Children = { heading, actions } },
                new TextBlock
                {
                    Text = entry.Preview(),
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Label,
                    TextWrapping = TextWrapping.Wrap,
                    [!TextBlock.ForegroundProperty] = Tokens.Live.InkMuted,
                },
            },
        };

        var status = _hotkeys?.StatusOf(entry.Id) ?? SnippetHotkeyStatus.None;
        if (entry.IsEnabled && SnippetHotkeyBinder.Describe(status, entry.Hotkey) is { } problem)
        {
            body.Children.Add(Panels.Notice(problem));
        }

        var card = Panels.Card(body);
        card.Padding = new Thickness(Tokens.Space.Roomy, Tokens.Space.Base);
        card.Opacity = entry.IsEnabled ? 1 : 0.5;

        card.PointerEntered += (_, _) => actions.Opacity = 1;
        card.PointerExited += (_, _) => actions.Opacity = 0;

        return card;
    }

    private void ShowEditor(SnippetEntry? entry)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var editor = new SnippetEditorWindow(entry, _hotkeys, _pasteLast);
        editor.Saved += (_, saved) =>
        {
            if (entry is null) _store.Add(saved); else _store.Update(saved);
        };

        _ = editor.ShowDialog(owner);
    }
}

/// <summary>Add or edit one prompt or snippet, with the trigger warnings shown live.</summary>
public sealed class SnippetEditorWindow : Window
{
    private readonly TextBox _trigger;
    private readonly TextBox _text;
    private readonly ToggleSwitch _wholeUtterance;
    private readonly ShortcutRecorder _shortcut;
    private readonly StackPanel _shortcutProblems;
    private readonly SoftButton _suggestion;
    private readonly SnippetHotkeyBinder? _hotkeys;
    private SnippetHotkeyStatus _shortcutStatus;
    private HotkeyChord? _suggested;
    private readonly StackPanel _warnings;
    private readonly Caption _length;
    private readonly SoftButton _save;
    private readonly Guid _id;
    private readonly bool _wasEnabled;

    /// <summary>Raised when the user saves.</summary>
    public event EventHandler<SnippetEntry>? Saved;

    /// <summary>Creates the editor for a new or existing prompt or snippet.</summary>
    /// <param name="entry">The entry to edit, or null for a new prompt.</param>
    /// <param name="hotkeys">Checks a shortcut as it is recorded; null where shortcuts are unavailable.</param>
    /// <param name="pasteLast">
    /// The paste-last shortcut, given back too while a shortcut is recorded; null where there is none.
    /// </param>
    /// <remarks>
    /// A new entry starts as a prompt — said on its own, pasted verbatim — because that is what
    /// most people save. An existing entry keeps whatever it was.
    /// </remarks>
    public SnippetEditorWindow(SnippetEntry? entry, SnippetHotkeyBinder? hotkeys = null, PasteLastTranscript? pasteLast = null)
    {
        _id = entry?.Id ?? Guid.NewGuid();
        _wasEnabled = entry?.IsEnabled ?? true;
        _hotkeys = hotkeys;

        Title = entry is null ? "New prompt" : "Edit prompt or snippet";
        Width = 540;
        Height = 680;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, Tokens.Live.Canvas);

        _trigger = Panels.Field("my address", entry?.Trigger ?? string.Empty);

        _text = Panels.Field(
            "12 Bridge Street\nManchester M1 2AB", entry?.Text ?? string.Empty);
        _text.AcceptsReturn = true;
        _text.TextWrapping = TextWrapping.Wrap;
        _text.Height = 190;
        _text.VerticalContentAlignment = VerticalAlignment.Top;
        _text.MaxLength = SnippetEntry.MaxLength;

        _length = new Caption();
        _warnings = new StackPanel { Spacing = Tokens.Space.Snug };

        _wholeUtterance = new ToggleSwitch
        {
            IsChecked = (entry?.Match ?? SnippetMatch.WholeUtterance) == SnippetMatch.WholeUtterance,
            OnContent = "On",
            OffContent = "Off",
            MinWidth = 0,
        };

        // Every combination Murmur holds is given back while recording, the paste-last one too:
        // Windows would otherwise swallow it and paste the last transcript into this editor.
        _shortcut = new ShortcutRecorder(
            entry?.Hotkey, hotkeys is null ? null : () => ShortcutSuspension.All(hotkeys, pasteLast));
        _shortcutProblems = new StackPanel { Spacing = Tokens.Space.Snug };

        _suggestion = Panels.QuietButton(string.Empty);
        _suggestion.HorizontalAlignment = HorizontalAlignment.Left;
        _suggestion.Click += (_, _) => _shortcut.Chord = _suggested;

        _trigger.TextChanged += (_, _) => Revalidate();
        _text.TextChanged += (_, _) => Revalidate();
        _wholeUtterance.IsCheckedChanged += (_, _) => Revalidate();
        _shortcut.ChordChanged += (_, _) =>
        {
            RecheckShortcut();
            Revalidate();
        };

        var cancel = Panels.OutlineButton("Cancel");
        cancel.Click += (_, _) => Close();

        _save = Panels.PrimaryButton("Save");
        _save.Click += (_, _) =>
        {
            if (!IsValid) return;
            Saved?.Invoke(this, Draft);
            Close();
        };

        Content = BuildContent(cancel);
        RecheckShortcut();
        Revalidate();
    }

    /// <summary>The entry as the fields currently describe it.</summary>
    public SnippetEntry Draft => new()
    {
        Id = _id,
        Trigger = (_trigger.Text ?? string.Empty).Trim(),
        Text = _text.Text ?? string.Empty,
        IsEnabled = _wasEnabled,
        Match = _wholeUtterance.IsChecked == true ? SnippetMatch.WholeUtterance : SnippetMatch.Inline,
        Hotkey = _shortcut.Chord,
    };

    /// <summary>The shortcut field.</summary>
    public ShortcutRecorder Shortcut => _shortcut;

    /// <summary>The "Say only this" switch.</summary>
    public ToggleSwitch WholeUtterance => _wholeUtterance;

    /// <summary>The free Ctrl+Alt+digit offered while there is no shortcut, or null.</summary>
    public HotkeyChord? SuggestedShortcut => _suggestion.IsVisible ? _suggested : null;

    /// <summary>The button that takes <see cref="SuggestedShortcut"/>.</summary>
    public SoftButton Suggestion => _suggestion;

    private bool IsValid =>
        Draft.Trigger.Length >= SnippetExpander.MinTriggerLength && Draft.Text.Trim().Length > 0
        && _shortcutStatus is not (SnippetHotkeyStatus.Invalid or SnippetHotkeyStatus.Duplicate or SnippetHotkeyStatus.PasteLast);

    /// <summary>Checks the recorded shortcut, or finds one to suggest when there is none.</summary>
    /// <remarks>
    /// <para>
    /// Only when the shortcut changes, not on every keystroke in the other fields: a check
    /// briefly registers the combination system-wide and waits on the hotkey thread to do it.
    /// </para>
    /// <para>
    /// A combination that cannot work, or that another prompt already has, blocks saving. One
    /// another app holds is allowed with a warning: that app may well be closed by the time it
    /// matters, and the list keeps saying so until it is.
    /// </para>
    /// </remarks>
    private void RecheckShortcut()
    {
        _shortcutStatus = _shortcut.Chord is not { } chord
            ? SnippetHotkeyStatus.None
            : HotkeyChords.Problem(chord) is not null
                ? SnippetHotkeyStatus.Invalid
                : _hotkeys?.Check(chord, _id) ?? SnippetHotkeyStatus.Active;

        _suggested = _shortcut.Chord is null ? _hotkeys?.Suggest(_id) : null;
        _suggestion.IsVisible = _suggested is not null;
        _suggestion.Content = _suggested is { } suggested ? $"Use {HotkeyChords.Format(suggested)}" : null;
    }

    private ScrollViewer BuildContent(Control cancel) => new()
    {
        Content = new StackPanel
        {
            Margin = new Thickness(Tokens.Space.Section),
            Spacing = Tokens.Space.Wide,
            Children =
            {
                Panels.Labelled("When you say", _trigger),
                new StackPanel
                {
                    Spacing = Tokens.Space.Snug,
                    Children =
                    {
                        new DockPanel
                        {
                            LastChildFill = true,
                            Children =
                            {
                                Panels.Docked(_length, Dock.Right),
                                Panels.FieldLabel("Paste this"),
                            },
                        },
                        _text,
                    },
                },
                new DockPanel
                {
                    LastChildFill = true,
                    Children =
                    {
                        Panels.Docked(_wholeUtterance, Dock.Right),
                        new TextBlock
                        {
                            Text = "Say only this — paste it exactly as written",
                            FontFamily = Tokens.Fonts.Ui,
                            FontSize = Tokens.Fonts.Body,
                            TextWrapping = TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center,
                            [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
                        },
                    },
                },
                Panels.Note("On: the trigger has to be all you say, and the text is pasted with "
                          + "every line break kept. Off: it expands wherever you say it, like a "
                          + "signature mid-sentence."),
                _warnings,
                Panels.Labelled("Shortcut (optional)", _shortcut),
                _suggestion,
                Panels.Note("Pastes it without speaking. Ctrl+Alt and a number is easy to reach "
                          + "with one hand."),
                _shortcutProblems,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Snug,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, _save },
                },
            },
        },
    };

    private void Revalidate()
    {
        var draft = Draft;

        _length.Text = $"{draft.Text.Length:N0} / {SnippetEntry.MaxLength:N0}";

        _warnings.Children.Clear();
        foreach (var warning in SnippetWarning.Check(draft))
        {
            _warnings.Children.Add(Panels.Notice(warning.Message));
        }

        _shortcutProblems.Children.Clear();
        if (SnippetHotkeyBinder.Describe(_shortcutStatus, draft.Hotkey) is { } problem)
        {
            _shortcutProblems.Children.Add(Panels.Notice(problem));
        }

        _save.IsEnabled = IsValid;
    }
}
