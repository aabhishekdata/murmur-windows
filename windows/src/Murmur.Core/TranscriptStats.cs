namespace Murmur.Core;

/// <summary>
/// The headline numbers shown on the Hub's home screen.
/// </summary>
/// <param name="Recordings">How many dictations have been kept.</param>
/// <param name="Words">Total words dictated.</param>
/// <param name="WordsPerMinute">
/// Speaking rate, averaged over every recording: total words divided by total audio time.
/// </param>
/// <param name="StreakDays">
/// Consecutive days up to and including the most recent dictation day, and only if that day is
/// today or yesterday — a streak that stopped two days ago is not a streak.
/// </param>
public readonly record struct DictationStats(
    int Recordings,
    int Words,
    int WordsPerMinute,
    int StreakDays);

/// <summary>
/// Derives the home-screen figures from transcript history.
/// </summary>
/// <remarks>
/// Kept out of the view so the arithmetic — particularly the streak, which has awkward
/// boundaries around "today", "yesterday" and a gap — can be tested against a fixed clock
/// rather than eyeballed on whatever day the app happens to be opened.
/// </remarks>
public static class TranscriptStats
{
    private static readonly char[] WordSeparators = [' ', '\t', '\n', '\r'];

    /// <summary>Counts the words in one transcript.</summary>
    /// <param name="text">The transcript.</param>
    /// <returns>Whitespace-delimited word count.</returns>
    public static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Summarises a history.</summary>
    /// <param name="records">Every kept dictation, in any order.</param>
    /// <param name="today">The current local date, so the streak is testable.</param>
    /// <returns>The figures for the home screen.</returns>
    public static DictationStats Summarise(IReadOnlyList<TranscriptRecord> records, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(records);

        var words = 0;
        var seconds = 0.0;

        foreach (var record in records)
        {
            words += CountWords(record.Text);
            seconds += record.AudioSeconds;
        }

        // Guard the divide rather than the multiply: a history of zero-length clips is a real
        // possibility (a key tapped rather than held) and must not produce infinity.
        var wpm = seconds > 0.5 ? (int)Math.Round(words / seconds * 60) : 0;

        return new DictationStats(records.Count, words, wpm, Streak(records, today));
    }

    /// <summary>
    /// Consecutive days of dictation ending today or yesterday.
    /// </summary>
    /// <param name="records">Every kept dictation.</param>
    /// <param name="today">The current local date.</param>
    /// <returns>The streak length in days, or zero if the run does not reach the present.</returns>
    public static int Streak(IReadOnlyList<TranscriptRecord> records, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return 0;

        var days = new HashSet<DateOnly>();
        foreach (var record in records) days.Add(DateOnly.FromDateTime(record.At.ToLocalTime().Date));

        // Yesterday still counts: it is early morning and the user has not dictated yet today,
        // and zeroing a real streak at midnight is the kind of detail that feels broken.
        var cursor = days.Contains(today)
            ? today
            : days.Contains(today.AddDays(-1)) ? today.AddDays(-1) : (DateOnly?)null;

        if (cursor is null) return 0;

        var streak = 0;
        var day = cursor.Value;

        while (days.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }

        return streak;
    }

    /// <summary>
    /// The heading a record belongs under when history is grouped chronologically.
    /// </summary>
    /// <param name="at">When the dictation happened.</param>
    /// <param name="today">The current local date.</param>
    /// <returns>"Today", "Yesterday", "This week", "This month" or "Earlier".</returns>
    public static string GroupFor(DateTimeOffset at, DateOnly today)
    {
        var day = DateOnly.FromDateTime(at.ToLocalTime().Date);
        var age = today.DayNumber - day.DayNumber;

        return age switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            <= 7 => "This week",
            <= 31 => "This month",
            _ => "Earlier",
        };
    }
}
