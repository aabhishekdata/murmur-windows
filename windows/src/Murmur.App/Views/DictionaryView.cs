using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Core;
using Murmur.Dictionary;

namespace Murmur.App.Views;

/// <summary>
/// The dictionary: add, edit, delete, search.
/// </summary>
/// <remarks>
/// Both entry kinds live in one list rather than separate tabs — they are two shapes of the
/// same idea, and you want to see everything you have taught it at once. The kind is carried by
/// a chip on each row.
/// </remarks>
public sealed class DictionaryView : UserControl
{
    private readonly DictionaryFile _file;
    private readonly TextBox _search;
    private readonly Debouncer _searchDebounce;
    private readonly StackPanel _list;
    private readonly Caption _count;

    /// <summary>Builds the view over <paramref name="file"/>.</summary>
    /// <param name="file">The dictionary on disk.</param>
    public DictionaryView(DictionaryFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;

        _search = Panels.SearchBox("Search dictionary");
        _searchDebounce = new Debouncer(Tokens.Motion.SearchDebounce, Refresh);
        _search.TextChanged += (_, _) => _searchDebounce.Trigger();

        var add = Panels.PrimaryButton("Add word");
        add.Click += (_, _) => ShowEditor(null);

        _list = new StackPanel
        {
            Spacing = Tokens.Space.Tight,
            Margin = new Thickness(Tokens.Space.Section, Tokens.Space.Tight, Tokens.Space.Section, Tokens.Space.Roomy),
        };

        _count = new Caption();

        var reveal = Panels.QuietButton("Open dictionary.txt");
        reveal.Click += (_, _) => OpenInEditor(_file.FilePath);

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Panels.Docked(Panels.SearchRow(_search, add), Dock.Top),
                Panels.Docked(Panels.Footer(_count, reveal), Dock.Bottom),
                new ScrollViewer { Content = _list },
            },
        };

        _file.Changed += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        var entries = _file.Search(_search.Text ?? string.Empty);

        _list.Children.Clear();
        _count.Text = _file.Entries.Count == 1 ? "1 entry" : $"{_file.Entries.Count} entries";

        if (entries.Count == 0)
        {
            _list.Children.Add(Panels.EmptyState(
                _file.Entries.Count == 0 ? "Dictionary empty" : "No matches",
                _file.Entries.Count == 0
                    ? "Add the words it keeps getting wrong, and the spellings you want instead."
                    : "Try a different search."));
            return;
        }

        foreach (var entry in entries) _list.Children.Add(BuildRow(entry));
    }

    private Border BuildRow(DictionaryEntry entry)
    {
        var edit = Panels.QuietButton("Edit");
        edit.Click += (_, _) => ShowEditor(entry);

        var toggle = Panels.QuietButton(entry.IsEnabled ? "Disable" : "Enable");
        toggle.Click += (_, _) => _file.Update(entry with { IsEnabled = !entry.IsEnabled });

        var delete = Panels.QuietButton("Delete");
        delete.Tone = ButtonTone.Danger;
        delete.Click += (_, _) => _file.Remove(entry.Id);

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Base,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                KindChip(entry.Kind),
                new TextBlock
                {
                    Text = entry.Kind == EntryKind.Correction
                        ? $"{entry.Hear}  →  {entry.Write}"
                        : entry.Write,
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Body,
                    VerticalAlignment = VerticalAlignment.Center,
                    [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
                },
            },
        };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Tight,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            Children = { edit, toggle, delete },
        };

        var row = new Border
        {
            CornerRadius = new CornerRadius(Tokens.Radius.Card),
            BorderThickness = new Thickness(Tokens.Border.Hairline),
            Padding = new Thickness(Tokens.Space.Roomy, Tokens.Space.Snug),
            MinHeight = Tokens.Space.Page,
            [!Border.BackgroundProperty] = Tokens.Live.Surface,
            [!Border.BorderBrushProperty] = Tokens.Live.Line,
            Opacity = entry.IsEnabled ? 1 : 0.5,
            Child = new Grid { Children = { left, right } },
        };

        row.PointerEntered += (_, _) => right.Opacity = 1;
        row.PointerExited += (_, _) => right.Opacity = 0;

        return row;
    }

    /// <summary>"Term" or "Fix", at one width so the words after it line up down the list.</summary>
    private static Border KindChip(EntryKind kind)
    {
        var chip = Panels.Chip(kind == EntryKind.Correction ? "Fix" : "Term", accent: kind == EntryKind.Correction);
        chip.MinWidth = Tokens.Space.Section + Tokens.Space.Snug;
        return chip;
    }

    private void ShowEditor(DictionaryEntry? entry)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var editor = new DictionaryEditorWindow(entry);
        editor.Saved += (_, saved) =>
        {
            if (entry is null) _file.Add(saved); else _file.Update(saved);
        };

        _ = editor.ShowDialog(owner);
    }

    /// <summary>Opens the dictionary in the user's default text editor.</summary>
    private static void OpenInEditor(string path)
    {
        try
        {
            // The file must exist before the shell will open it — a brand-new install has
            // never saved one.
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }

            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Opening an editor is a convenience; failing to must never take the app down.
        }
    }
}

/// <summary>
/// Add or edit one dictionary entry, with the false-positive warning shown live.
/// </summary>
public sealed class DictionaryEditorWindow : Window
{
    private readonly SegmentedControl _kindSelector;
    private readonly TextBox _hear;
    private readonly TextBox _write;
    private readonly StackPanel _warnings;
    private readonly SoftButton _save;
    private readonly Guid _id;
    private readonly bool _wasEnabled;

    private StackPanel? _hearField;
    private TextBlock? _writeLabel;
    private EntryKind _kind;

    /// <summary>Raised when the user saves.</summary>
    public event EventHandler<DictionaryEntry>? Saved;

    /// <summary>Creates the editor for a new or existing entry.</summary>
    /// <param name="entry">The entry to edit, or null for a new one.</param>
    public DictionaryEditorWindow(DictionaryEntry? entry)
    {
        _id = entry?.Id ?? Guid.NewGuid();
        _wasEnabled = entry?.IsEnabled ?? true;
        _kind = entry?.Kind ?? EntryKind.Term;

        Title = entry is null ? "New entry" : "Edit entry";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, Tokens.Live.Canvas);

        _kindSelector = new SegmentedControl("Term", "Correction");
        _kindSelector.SelectionChanged += (_, index) => SetKind(index == 1 ? EntryKind.Correction : EntryKind.Term);

        _hear = Panels.Field("cloud code", entry?.Hear ?? string.Empty);
        _write = Panels.Field("Claude Code", entry?.Write ?? string.Empty);
        _hear.TextChanged += (_, _) => Revalidate();
        _write.TextChanged += (_, _) => Revalidate();

        _warnings = new StackPanel { Spacing = Tokens.Space.Snug };

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
        SetKind(_kind);
    }

    private DictionaryEntry Draft => new()
    {
        Id = _id,
        Kind = _kind,
        Write = (_write.Text ?? string.Empty).Trim(),
        Hear = _kind == EntryKind.Correction ? (_hear.Text ?? string.Empty).Trim() : string.Empty,
        IsEnabled = _wasEnabled,
    };

    private bool IsValid =>
        Draft.Write.Length > 0 && (_kind == EntryKind.Term || Draft.Hear.Length > 0);

    private StackPanel BuildContent(Control cancel)
    {
        _hearField = Panels.Labelled("When you hear", _hear);
        _hearField.IsVisible = _kind == EntryKind.Correction;

        _writeLabel = Panels.FieldLabel("Word or phrase");

        return new StackPanel
        {
            Margin = new Thickness(Tokens.Space.Section),
            Spacing = Tokens.Space.Wide,
            Children =
            {
                _kindSelector,
                _hearField,
                new StackPanel { Spacing = Tokens.Space.Tight + Tokens.Space.Hair, Children = { _writeLabel, _write } },
                _warnings,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Snug,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, _save },
                },
            },
        };
    }

    private void SetKind(EntryKind kind)
    {
        _kind = kind;
        _kindSelector.Select(kind == EntryKind.Correction ? 1 : 0);

        if (_hearField is not null) _hearField.IsVisible = kind == EntryKind.Correction;
        if (_writeLabel is not null) _writeLabel.Text = kind == EntryKind.Correction ? "Write" : "Word or phrase";

        Revalidate();
    }

    private void Revalidate()
    {
        _warnings.Children.Clear();

        foreach (var warning in DictionaryWarning.Check(Draft))
        {
            _warnings.Children.Add(Panels.Notice(warning.Message));
        }

        _save.IsEnabled = IsValid;
    }
}
