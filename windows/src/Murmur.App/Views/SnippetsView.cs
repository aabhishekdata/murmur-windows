using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>
/// Snippets: a spoken phrase in, a stored block of text out.
/// </summary>
/// <remarks>
/// Built to the same shape as <see cref="DictionaryView"/> on purpose — the two features are
/// structurally identical from the user's side (stored entries, a search, a row per entry, an
/// editor dialog), and making them look and behave differently would be inventing a second way
/// to do the same thing.
/// </remarks>
public sealed class SnippetsView : UserControl
{
    private readonly SnippetStore _store;
    private readonly TextBox _search;
    private readonly StackPanel _list;
    private readonly Caption _count;

    /// <summary>Builds the view over <paramref name="store"/>.</summary>
    /// <param name="store">The snippet store.</param>
    public SnippetsView(SnippetStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;

        _search = Panels.SearchBox("Search snippets");
        _search.TextChanged += (_, _) => Refresh();

        var add = Panels.PrimaryButton("New snippet");
        add.Click += (_, _) => ShowEditor(null);

        _list = new StackPanel
        {
            Spacing = Tokens.Space.Snug,
            Margin = new Thickness(Tokens.Space.Wide, Tokens.Space.Roomy),
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
        Refresh();
    }

    private static Caption Hint() => new()
    {
        Text = "Say the trigger anywhere in a dictation",
        Foreground = Tokens.Brushes.InkFaint,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void Refresh()
    {
        var entries = _store.Search(_search.Text ?? string.Empty);

        _list.Children.Clear();
        _count.Text = _store.Entries.Count == 1 ? "1 snippet" : $"{_store.Entries.Count} snippets";

        if (entries.Count == 0)
        {
            _list.Children.Add(Panels.EmptyState(
                _store.Entries.Count == 0 ? "No snippets yet" : "No matches",
                _store.Entries.Count == 0
                    ? "Store a block of text behind a spoken phrase — an address, a signature, "
                    + "a standard reply — and say the phrase to type all of it."
                    : "Try a different search."));
            return;
        }

        foreach (var entry in entries) _list.Children.Add(BuildRow(entry));
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
                Panels.Chip("Say", Tokens.Colors.Accent),
                new TextBlock
                {
                    Text = entry.Trigger,
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Body,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Tokens.Brushes.Ink,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new Caption
                {
                    Text = entry.Text.Length == 1 ? "1 character" : $"{entry.Text.Length:N0} characters",
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

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
                    Foreground = Tokens.Brushes.InkMuted,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

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

        var editor = new SnippetEditorWindow(entry);
        editor.Saved += (_, saved) =>
        {
            if (entry is null) _store.Add(saved); else _store.Update(saved);
        };

        _ = editor.ShowDialog(owner);
    }
}

/// <summary>Add or edit one snippet, with the trigger warnings shown live.</summary>
public sealed class SnippetEditorWindow : Window
{
    private readonly TextBox _trigger;
    private readonly TextBox _text;
    private readonly StackPanel _warnings;
    private readonly Caption _length;
    private readonly SoftButton _save;
    private readonly Guid _id;
    private readonly bool _wasEnabled;

    /// <summary>Raised when the user saves.</summary>
    public event EventHandler<SnippetEntry>? Saved;

    /// <summary>Creates the editor for a new or existing snippet.</summary>
    /// <param name="entry">The snippet to edit, or null for a new one.</param>
    public SnippetEditorWindow(SnippetEntry? entry)
    {
        _id = entry?.Id ?? Guid.NewGuid();
        _wasEnabled = entry?.IsEnabled ?? true;

        Title = entry is null ? "New snippet" : "Edit snippet";
        Width = 540;
        Height = 520;
        CanResize = false;
        Background = Tokens.Brushes.Canvas;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

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

        _trigger.TextChanged += (_, _) => Revalidate();
        _text.TextChanged += (_, _) => Revalidate();

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
        Revalidate();
    }

    private SnippetEntry Draft => new()
    {
        Id = _id,
        Trigger = (_trigger.Text ?? string.Empty).Trim(),
        Text = _text.Text ?? string.Empty,
        IsEnabled = _wasEnabled,
    };

    private bool IsValid =>
        Draft.Trigger.Length >= SnippetExpander.MinTriggerLength && Draft.Text.Trim().Length > 0;

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
                                new Caption { Text = "Type this", IsHeader = true },
                            },
                        },
                        _text,
                    },
                },
                _warnings,
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

        _save.IsEnabled = IsValid;
    }
}
