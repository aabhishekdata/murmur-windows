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
/// Corrections Murmur noticed it should have made, offered one click away.
/// </summary>
/// <remarks>
/// <para>
/// This is the honest half of "the dictionary learns by itself". Murmur cannot watch the user
/// fix a word in another application — <c>SendInput</c> is write-only and reading the text back
/// would mean walking somebody else's UI Automation tree — so instead it uses the signal it
/// already has on disk: the user has told it which words matter, and a transcript containing
/// something <i>nearly</i> one of those words is probably that word misheard.
/// </para>
/// <para>
/// Nothing here is applied automatically. Accepting a suggestion writes an ordinary correction
/// entry, visible in the Dictionary section and deletable like any other — there is no second,
/// hidden dictionary. Turning one down is remembered, so a rejected suggestion does not come
/// back every time the Hub is opened.
/// </para>
/// </remarks>
public sealed class SuggestionsView : UserControl
{
    private readonly AppSettings _settings;
    private readonly DictionaryFile _dictionary;
    private readonly TranscriptStore _transcripts;
    private readonly StackPanel _list;

    /// <summary>Builds the strip.</summary>
    /// <param name="settings">Where dismissals are remembered.</param>
    /// <param name="dictionary">The dictionary a suggestion would be added to.</param>
    /// <param name="transcripts">The history suggestions are mined from.</param>
    public SuggestionsView(AppSettings settings, DictionaryFile dictionary, TranscriptStore transcripts)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(transcripts);

        _settings = settings;
        _dictionary = dictionary;
        _transcripts = transcripts;

        _list = new StackPanel { Spacing = Tokens.Space.Snug };
        Content = _list;

        // Both stores raise Changed from whichever thread wrote to them, and this rebuilds live
        // controls. Composition marshals, but the view defends itself too — the same discipline
        // that keeps a background-thread transcript Add from becoming a swallowed cross-thread
        // exception.
        _dictionary.Changed += OnChanged;
        _transcripts.Changed += OnChanged;

        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        _list.Children.Clear();

        var dismissed = new HashSet<string>(
            _settings.Data.DismissedSuggestions, StringComparer.OrdinalIgnoreCase);

        var suggestions = DictionarySuggestions.For(
            _transcripts.Records, _dictionary.Entries, dismissed);

        IsVisible = suggestions.Count > 0;
        if (suggestions.Count == 0) return;

        _list.Children.Add(new Caption
        {
            Text = suggestions.Count == 1
                ? "1 suggested correction"
                : $"{suggestions.Count} suggested corrections",
            IsHeader = true,
        });

        foreach (var suggestion in suggestions) _list.Children.Add(BuildRow(suggestion));
    }

    private Border BuildRow(DictionarySuggestion suggestion)
    {
        var add = Panels.OutlineButton("Add to dictionary");
        add.Click += (_, _) =>
        {
            _dictionary.Add(suggestion.ToEntry());
            Refresh();
        };

        var dismiss = Panels.QuietButton("Not a mistake");
        dismiss.Click += (_, _) =>
        {
            Dismiss(suggestion.Key);
            Refresh();
        };

        var summary = new StackPanel
        {
            Spacing = Tokens.Space.Tight,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = $"{suggestion.Heard}  →  {suggestion.Intended}",
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Body,
                    TextWrapping = TextWrapping.Wrap,
                    [!TextBlock.ForegroundProperty] = Tokens.Live.Ink,
                },
                new Caption
                {
                    Text = suggestion.Occurrences == 1
                        ? $"Heard once, and \"{suggestion.Intended}\" is in your dictionary"
                        : $"Heard {suggestion.Occurrences} times, and \"{suggestion.Intended}\" "
                          + "is in your dictionary",
                },
            },
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { dismiss, add },
        };

        return new Border
        {
            CornerRadius = new CornerRadius(Tokens.Radius.Card),
            BorderThickness = new Thickness(Tokens.Border.Hairline),
            Padding = new Thickness(Tokens.Space.Roomy, Tokens.Space.Base),
            [!Border.BackgroundProperty] = Tokens.Live.AccentSoft,
            [!Border.BorderBrushProperty] = Tokens.Live.Line,
            Child = new Grid { Children = { summary, actions } },
        };
    }

    private void Dismiss(string key)
    {
        var kept = _settings.Data.DismissedSuggestions;
        if (kept.Contains(key, StringComparer.OrdinalIgnoreCase)) return;

        try
        {
            _settings.Update(_settings.Data with { DismissedSuggestions = [.. kept, key] });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A dismissal that cannot be saved is a suggestion that comes back next time. That
            // is annoying; taking the app down over it would be worse.
            CrashLog.Write("Saving a dismissed suggestion failed", ex);
        }
    }
}

/// <summary>
/// Turns one transcript into a dictionary correction in a couple of clicks.
/// </summary>
/// <remarks>
/// The other half of "learning": when Murmur has <i>not</i> spotted the mistake, the user is
/// looking straight at it in their history, and the shortest honest path from "that word is
/// wrong" to "never do that again" is a button on the row. The wrong text is pre-filled from the
/// transcript, so the only thing left to type is the spelling they wanted.
/// </remarks>
public sealed class TeachCorrectionWindow : Window
{
    private readonly TextBox _hear;
    private readonly TextBox _write;
    private readonly Caption _backfill;
    private readonly SoftButton _save;
    private readonly IReadOnlyList<TranscriptRecord> _records;

    /// <summary>Raised when the user saves a correction.</summary>
    public event EventHandler<Murmur.Dictionary.DictionaryEntry>? Saved;

    /// <summary>Builds the dialog over one transcript.</summary>
    /// <param name="heard">The text as transcribed, used to pre-fill the trigger.</param>
    /// <param name="records">History, for the "this would have fixed N" count.</param>
    public TeachCorrectionWindow(string heard, IReadOnlyList<TranscriptRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;

        Title = "Teach a correction";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, Tokens.Live.Canvas);

        _hear = Panels.Field("what it heard", heard ?? string.Empty);
        _write = Panels.Field("what you meant", string.Empty);
        _backfill = new Caption();

        _hear.TextChanged += (_, _) => Revalidate();
        _write.TextChanged += (_, _) => Revalidate();

        var cancel = Panels.OutlineButton("Cancel");
        cancel.Click += (_, _) => Close();

        _save = Panels.PrimaryButton("Add correction");
        _save.Click += (_, _) =>
        {
            if (!IsValid) return;
            Saved?.Invoke(this, Murmur.Dictionary.DictionaryEntry.Correction(Hear, Write));
            Close();
        };

        Content = new StackPanel
        {
            Margin = new Thickness(Tokens.Space.Section),
            Spacing = Tokens.Space.Wide,
            Children =
            {
                Panels.Note("Pick out the words Murmur got wrong and type what you meant. "
                          + "It becomes an ordinary dictionary entry you can edit later."),
                Panels.Labelled("When you hear", _hear),
                Panels.Labelled("Write", _write),
                _backfill,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Snug,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, _save },
                },
            },
        };

        Revalidate();
    }

    private string Hear => (_hear.Text ?? string.Empty).Trim();

    private string Write => (_write.Text ?? string.Empty).Trim();

    private bool IsValid =>
        Hear.Length > 0 &&
        Write.Length > 0 &&
        !string.Equals(Hear, Write, StringComparison.OrdinalIgnoreCase);

    private void Revalidate()
    {
        // "Does this rule actually do anything?" answered before the next dictation rather than
        // after it. A count of zero usually means the trigger has a typo in it.
        var matches = Hear.Length == 0 ? 0 : DictionarySuggestions.Backfill(_records, Hear);

        _backfill.Text = matches switch
        {
            0 when Hear.Length == 0 => string.Empty,
            0 => "No past transcript contains that, so check the spelling of what it heard.",
            1 => "1 past transcript contains that phrase.",
            _ => $"{matches} past transcripts contain that phrase.",
        };

        _save.IsEnabled = IsValid;
    }
}
