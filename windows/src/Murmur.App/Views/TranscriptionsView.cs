using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>
/// Past transcriptions: searchable, grouped by day, each copyable and deletable.
/// </summary>
/// <remarks>
/// <para>
/// Rows show which dictionary corrections fired. Without that the dictionary is invisible and
/// there is no way to tell a rule that works from one that never matches.
/// </para>
/// <para>
/// The row actions are revealed on hover rather than shown permanently. A history is something
/// you read; four buttons per row turn it into a control panel.
/// </para>
/// </remarks>
public sealed class TranscriptionsView : UserControl
{
    private readonly TranscriptStore _store;
    private readonly DictionaryFile? _dictionary;
    private readonly TextBox _search;
    private readonly StackPanel _list;
    private readonly Caption _count;
    private readonly SoftButton _clear;

    /// <summary>Builds the view over <paramref name="store"/>.</summary>
    /// <param name="store">Transcript history.</param>
    /// <param name="dictionary">
    /// The dictionary a row's "Teach" action writes into. Null hides that action — a view over
    /// history alone still works, which is what the marshalling regression tests use.
    /// </param>
    public TranscriptionsView(TranscriptStore store, DictionaryFile? dictionary = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _dictionary = dictionary;

        _search = Panels.SearchBox("Search transcripts");
        _search.TextChanged += (_, _) => Refresh();

        _list = new StackPanel
        {
            Spacing = Tokens.Space.Snug,
            Margin = new Thickness(Tokens.Space.Wide, Tokens.Space.Roomy),
        };

        _count = new Caption();

        _clear = Panels.QuietButton("Delete all");
        _clear.Tone = ButtonTone.Danger;
        _clear.Click += (_, _) => { _store.Clear(); Refresh(); };

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Panels.Docked(Panels.SearchRow(_search), Dock.Top),
                Panels.Docked(Panels.Footer(_count, _clear), Dock.Bottom),
                new ScrollViewer { Content = _list },
            },
        };

        // Changed can fire from any thread — the store itself does no marshalling — and
        // Refresh rebuilds live controls, which Avalonia only permits on the UI thread.
        // Callers are expected to marshal (Composition does), but this view must never be
        // the thing that turns a background-thread Add into a swallowed cross-thread
        // exception again, so it defends itself too.
        _store.Changed += (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess()) Refresh();
            else Dispatcher.UIThread.Post(Refresh);
        };

        Refresh();
    }

    private void Refresh()
    {
        var records = _store.Search(_search.Text ?? string.Empty);

        _list.Children.Clear();
        _count.Text = _store.Records.Count == 1 ? "1 recording" : $"{_store.Records.Count} recordings";

        // A live destructive action with nothing to destroy is just a red word on an empty
        // screen.
        _clear.IsEnabled = _store.Records.Count > 0;

        if (records.Count == 0)
        {
            _list.Children.Add(Panels.EmptyState(
                _store.Records.Count == 0 ? "No recordings yet" : "No matches",
                _store.Records.Count == 0
                    ? "Hold the push-to-talk key and speak. What you dictate shows up here."
                    : "Try a different search."));
            return;
        }

        // Grouped chronologically, with the heading printed once per run rather than on every
        // row — the point of the grouping is to make the shape of a day visible at a glance.
        var today = DateOnly.FromDateTime(DateTime.Now);
        string? group = null;

        foreach (var record in records)
        {
            var heading = TranscriptStats.GroupFor(record.At, today);
            if (heading != group)
            {
                group = heading;
                _list.Children.Add(new Caption
                {
                    Text = heading,
                    IsHeader = true,
                    Margin = new Thickness(
                        Tokens.Space.Tight,
                        _list.Children.Count == 0 ? 0 : Tokens.Space.Base,
                        0,
                        Tokens.Space.Tight),
                });
            }

            _list.Children.Add(BuildRow(record));
        }
    }

    private Border BuildRow(TranscriptRecord record)
    {
        var copy = Panels.QuietButton("Copy");
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(record.Text).ConfigureAwait(true);

            copy.Content = "Copied";
            await Task.Delay(TimeSpan.FromSeconds(1.4)).ConfigureAwait(true);
            copy.Content = "Copy";
        };

        var delete = Panels.QuietButton("Delete");
        delete.Tone = ButtonTone.Danger;
        delete.Click += (_, _) => _store.Remove(record.Id);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Tight,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0,
            Children = { copy, delete },
        };

        // "Murmur got a word wrong" is a thought the user has while looking at this row, and
        // this is the shortest honest path from there to a dictionary rule. It is the manual
        // half of learning; SuggestionsView is the automatic half.
        if (_dictionary is not null)
        {
            var teach = Panels.QuietButton("Teach");
            teach.Click += (_, _) => ShowTeach(record);
            actions.Children.Insert(0, teach);
        }

        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Base,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Caption
                {
                    Text = record.At.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = record.ProcessingSeconds.ToString("0.0", CultureInfo.CurrentCulture) + "s",
                    FontFamily = Tokens.Fonts.Mono,
                    FontSize = Tokens.Fonts.Caption,
                    Foreground = Tokens.Brushes.InkFaint,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        var body = new StackPanel
        {
            Spacing = Tokens.Space.Snug,
            Children =
            {
                new Grid { Children = { meta, actions } },
                Panels.Body(record.Text),
            },
        };

        if (record.Corrections is { Count: > 0 } corrections)
        {
            body.Children.Add(BuildCorrectionChips(corrections));
        }

        var card = Panels.Card(body);
        card.Padding = new Thickness(Tokens.Space.Roomy, Tokens.Space.Base);

        // Hover to reveal. Opacity rather than IsVisible: a control that appears and disappears
        // changes the row's height, and the list would jump under the pointer.
        card.PointerEntered += (_, _) => actions.Opacity = 1;
        card.PointerExited += (_, _) => actions.Opacity = 0;

        return card;
    }

    private void ShowTeach(TranscriptRecord record)
    {
        if (_dictionary is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var editor = new TeachCorrectionWindow(record.Text, _store.Records);
        editor.Saved += (_, entry) => _dictionary.Add(entry);

        _ = editor.ShowDialog(owner);
    }

    /// <summary>Shows that the dictionary fired, and on what.</summary>
    private static WrapPanel BuildCorrectionChips(IReadOnlyList<Dictionary.AppliedCorrection> corrections)
    {
        var row = new WrapPanel { ItemSpacing = Tokens.Space.Tight, LineSpacing = Tokens.Space.Tight };

        foreach (var correction in corrections)
        {
            var label = correction.Count > 1
                ? $"{correction.From} → {correction.To} ×{correction.Count}"
                : $"{correction.From} → {correction.To}";

            row.Children.Add(Panels.Chip(label, Tokens.Colors.Accent));
        }

        return row;
    }
}
