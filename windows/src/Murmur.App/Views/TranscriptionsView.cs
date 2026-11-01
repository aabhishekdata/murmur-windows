using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Abstractions;
using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>
/// Past transcriptions: searchable, grouped by day, each one copyable, pasteable, retryable with
/// the other engine, and deletable.
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
/// <para>
/// Each row says which engine heard it, so "did this leave my PC?" has an answer after the fact.
/// <b>Retry with the other engine</b> is offered only for the last few dictations, whose audio is
/// still in memory, and only towards an engine the privacy contract allows right now
/// (<see cref="TranscriptRetry"/>). <b>Paste</b> minimises the hub first, so the text goes to the
/// window that was in front of it rather than into Murmur.
/// </para>
/// <para>
/// The list is virtualised: rows are built only for what is on screen. A year of daily
/// dictation is thousands of cards, and building every one of them — on open, and again on
/// every keystroke in the search box — is what made the hub slow to appear. Search is also
/// debounced, so typing a word filters once rather than once per letter.
/// </para>
/// </remarks>
public sealed class TranscriptionsView : UserControl
{
    private readonly TranscriptStore _store;
    private readonly DictionaryFile? _dictionary;
    private readonly Composition? _composition;
    private readonly TextBox _search;
    private readonly Debouncer _searchDebounce;
    private readonly ItemsControl _list;
    private readonly ContentControl _empty;
    private readonly Caption _count;
    private readonly SoftButton _clear;

    /// <summary>Builds the view over <paramref name="store"/>.</summary>
    /// <param name="store">Transcript history.</param>
    /// <param name="dictionary">
    /// The dictionary a row's "Teach" action writes into. Null hides that action — a view over
    /// history alone still works, which is what the marshalling regression tests use.
    /// </param>
    /// <param name="composition">
    /// Where Paste and Retry go, and the kept audio a delete also forgets. Null hides both actions.
    /// </param>
    public TranscriptionsView(TranscriptStore store, DictionaryFile? dictionary = null, Composition? composition = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _dictionary = dictionary;
        _composition = composition;

        _search = Panels.SearchBox("Search transcripts");
        _searchDebounce = new Debouncer(Tokens.Motion.SearchDebounce, Refresh);
        _search.TextChanged += (_, _) => _searchDebounce.Trigger();

        // supportsRecycling: false, because a row's controls close over its record. A recycled
        // card would keep the Copy and Delete handlers of whatever it showed before.
        _list = new ItemsControl
        {
            Margin = new Thickness(Tokens.Space.Section, Tokens.Space.Tight, Tokens.Space.Section, Tokens.Space.Roomy),
            ItemsPanel = new FuncTemplate<Panel?>(static () => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<ListItem>(
                (item, _) => item is null ? null : BuildItem(item), supportsRecycling: false),
        };

        _empty = new ContentControl
        {
            Margin = new Thickness(Tokens.Space.Section, Tokens.Space.Roomy),
        };

        _count = new Caption();

        _clear = Panels.QuietButton("Delete all");
        _clear.Tone = ButtonTone.Danger;
        _clear.Click += (_, _) =>
        {
            // The kept audio goes with the history it belonged to.
            _composition?.Audio.Clear();
            _store.Clear();
            Refresh();
        };

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Panels.Docked(Panels.SearchRow(_search), Dock.Top),
                Panels.Docked(Panels.Footer(_count, _clear), Dock.Bottom),
                new ScrollViewer { Content = new Panel { Children = { _empty, _list } } },
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

    /// <summary>Whether a search is waiting for typing to pause. Exposed for tests.</summary>
    internal bool IsSearchPending => _searchDebounce.IsPending;

    /// <summary>Applies a pending search immediately. Exposed for tests.</summary>
    internal void ApplySearchNow() => _searchDebounce.Flush();

    /// <summary>How many rows (headings and cards) the list holds. Exposed for tests.</summary>
    internal int ItemCount => _list.ItemCount;

    /// <summary>One entry in the list: a day heading or a transcript card.</summary>
    /// <param name="Heading">The day, for a heading.</param>
    /// <param name="Record">The transcript, for a card.</param>
    /// <param name="IsFirst">Whether this is the top of the list, which takes no spacing above.</param>
    private sealed record ListItem(string? Heading, TranscriptRecord? Record, bool IsFirst);

    private void Refresh()
    {
        var records = _store.Search(_search.Text ?? string.Empty);

        _count.Text = _store.Records.Count == 1 ? "1 recording" : $"{_store.Records.Count} recordings";

        // A live destructive action with nothing to destroy is just a red word on an empty
        // screen.
        _clear.IsEnabled = _store.Records.Count > 0;

        if (records.Count == 0)
        {
            _list.ItemsSource = null;
            _list.IsVisible = false;
            _empty.IsVisible = true;
            _empty.Content = Panels.EmptyState(
                _store.Records.Count == 0 ? "No recordings yet" : "No matches",
                _store.Records.Count == 0
                    ? "Hold the push-to-talk key and speak. What you dictate shows up here."
                    : "Try a different search.");
            return;
        }

        // Grouped chronologically, with the heading printed once per run rather than on every
        // row — the point of the grouping is to make the shape of a day visible at a glance.
        // Only the cheap description of each row is built here; the controls are built by the
        // panel as rows scroll into view.
        var today = DateOnly.FromDateTime(DateTime.Now);
        var items = new List<ListItem>(records.Count + 8);
        string? group = null;

        foreach (var record in records)
        {
            var heading = TranscriptStats.GroupFor(record.At, today);
            if (heading != group)
            {
                group = heading;
                items.Add(new ListItem(heading, null, items.Count == 0));
            }

            items.Add(new ListItem(null, record, items.Count == 0));
        }

        _empty.IsVisible = false;
        _empty.Content = null;
        _list.IsVisible = true;
        _list.ItemsSource = items;
    }

    /// <summary>Builds the control for one list entry, as it scrolls into view.</summary>
    /// <remarks>
    /// The virtualising panel has no <c>Spacing</c>, so the gap the old stack panel put between
    /// children is a top margin on everything but the first entry — the same layout, pixel for
    /// pixel.
    /// </remarks>
    private Control BuildItem(ListItem item)
    {
        var gap = item.IsFirst ? 0 : Tokens.Space.Snug;

        if (item.Record is { } record)
        {
            var row = BuildRow(record);
            row.Margin = new Thickness(0, gap, 0, 0);
            return row;
        }

        return new Caption
        {
            Text = item.Heading,
            IsHeader = true,
            Margin = new Thickness(
                Tokens.Space.Tight,
                item.IsFirst ? 0 : Tokens.Space.Base + gap,
                0,
                Tokens.Space.Tight),
        };
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
        delete.Click += (_, _) =>
        {
            _composition?.Audio.Remove(record.Id);
            _store.Remove(record.Id);
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Tight,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0,
            Children = { copy, delete },
        };

        if (_composition is not null)
        {
            var paste = Panels.QuietButton("Paste");
            paste.Click += async (_, _) => await PasteAsync(record);
            actions.Children.Insert(0, paste);

            if (_composition.Retry?.TargetFor(record) is { } target)
            {
                var retry = Panels.QuietButton(RetryLabel(target));
                retry.Click += async (_, _) => await RetryAsync(record, retry, target);
                actions.Children.Insert(0, retry);
            }
        }

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
                    VerticalAlignment = VerticalAlignment.Center,
                    [!TextBlock.ForegroundProperty] = Tokens.Live.InkFaint,
                },
            },
        };

        if (EngineCaption(record) is { } heardBy)
        {
            meta.Children.Add(new Caption { Text = heardBy, VerticalAlignment = VerticalAlignment.Center });
        }

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

    /// <summary>Who heard a record, for its row; null for history written before it was recorded.</summary>
    /// <param name="record">The record.</param>
    /// <returns>"On this PC", "Azure", "Offline fallback", with "retried" when it is one.</returns>
    internal static string? EngineCaption(TranscriptRecord record)
    {
        var heard = record.Engine switch
        {
            TranscriberKind.Parakeet when record.FellBack => "Offline fallback",
            TranscriberKind.Parakeet => "On this PC",
            TranscriberKind.Azure => "Azure",
            _ => null,
        };

        if (heard is null) return null;
        return record.RetriedFrom is null ? heard : $"{heard} · retried";
    }

    private static string RetryLabel(TranscriberKind target) =>
        target == TranscriberKind.Azure ? "Retry with Azure" : "Retry with Parakeet";

    /// <summary>Pastes a row's text into the window that was in front of the hub.</summary>
    /// <remarks>
    /// The hub has focus while the button is clicked, so it gets out of the way first: minimised,
    /// Windows hands focus back to the window below it, and the paste lands there.
    /// </remarks>
    private async Task PasteAsync(TranscriptRecord record)
    {
        if (_composition is null) return;

        try
        {
            if (TopLevel.GetTopLevel(this) is Window window) window.WindowState = WindowState.Minimized;
            await Task.Delay(Tokens.Motion.FocusReturn).ConfigureAwait(true);
            await _composition.PasteLast.PasteAsync(record.Text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // An async void click handler sits above this; nothing may escape it.
            CrashLog.Write("Pasting from history failed", ex);
        }
    }

    /// <summary>Transcribes a row again with the other engine, and puts the result in its place.</summary>
    private async Task RetryAsync(TranscriptRecord record, SoftButton button, TranscriberKind target)
    {
        if (_composition?.Retry is not { } retry) return;

        button.IsEnabled = false;
        button.Content = "Retrying…";

        try
        {
            var replacement = await retry.RetryAsync(record, CancellationToken.None).ConfigureAwait(true);
            if (replacement is null)
            {
                button.Content = target == TranscriberKind.Azure ? "Azure heard nothing" : "Parakeet heard nothing";
                return;
            }

            // Back on the UI thread, which owns the history: the list rebuilds from its event.
            if (retry.Apply(_store, record, replacement) && ReferenceEquals(_store.Latest, replacement))
            {
                _composition.PasteLast.Remember(replacement.Text);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Retrying a transcript failed", ex);
            button.Content = "Retry failed";
            button.IsEnabled = true;
        }
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

            row.Children.Add(Panels.Chip(label, accent: true));
        }

        return row;
    }
}
