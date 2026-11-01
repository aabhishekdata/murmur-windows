using Murmur.Abstractions;

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

/// <summary>How much one engine heard.</summary>
/// <param name="Engine">The engine.</param>
/// <param name="Dictations">How many dictations it heard.</param>
/// <param name="Words">How many words they came to.</param>
/// <param name="AudioMinutes">How many minutes of audio it was given.</param>
public readonly record struct EngineUsage(TranscriberKind Engine, int Dictations, int Words, double AudioMinutes);

/// <summary>The figures on Home's usage dashboard.</summary>
/// <param name="Dictations">Dictations kept.</param>
/// <param name="Words">Words dictated.</param>
/// <param name="AudioMinutes">Minutes of audio, all engines.</param>
/// <param name="TimeSaved">Typing time saved, net of speaking and waiting; never negative.</param>
/// <param name="EstimatedCloudCost">US dollars, at the user's own per-minute prices. An estimate.</param>
/// <param name="FellBack">Dictations Parakeet answered because Azure could not.</param>
/// <param name="ByEngine">One entry per engine that heard anything, most minutes first.</param>
public sealed record UsageSummary(
    int Dictations,
    int Words,
    double AudioMinutes,
    TimeSpan TimeSaved,
    double EstimatedCloudCost,
    int FellBack,
    IReadOnlyList<EngineUsage> ByEngine);

/// <summary>
/// Works out Home's usage figures: words, minutes, time saved, the split by engine, and what the
/// cloud minutes would cost.
/// </summary>
/// <remarks>
/// <para>
/// <b>Time saved</b> is, per dictation, the time typing it would have taken at the user's typing
/// speed, less the time spent saying it and waiting for it: <c>words ÷ wpm − audio − processing</c>.
/// Summed, and shown as zero rather than as a negative number when dictating has not paid yet.
/// </para>
/// <para>
/// <b>Cloud cost</b> is minutes heard by Azure times the user's own per-minute price for the profile
/// that heard them; older history with no profile is priced as Accurate, the dearer of the two.
/// It is an estimate for a bill Murmur never sees, and is labelled so wherever it is shown.
/// Dictations Parakeet answered — including fallbacks — cost nothing.
/// </para>
/// </remarks>
public static class UsageStats
{
    /// <summary>A typical typing speed.</summary>
    public const int DefaultTypingWordsPerMinute = 40;

    /// <summary>The published per-minute estimate for <c>gpt-4o-transcribe</c>, in US dollars.</summary>
    public const double DefaultPricePerMinuteAccurate = 0.006;

    /// <summary>The published per-minute estimate for <c>gpt-4o-mini-transcribe</c>, in US dollars.</summary>
    public const double DefaultPricePerMinuteFast = 0.003;

    /// <summary>Summarises <paramref name="records"/>.</summary>
    /// <param name="records">Every kept dictation.</param>
    /// <param name="typingWordsPerMinute">The user's typing speed; anything below 1 means the default.</param>
    /// <param name="pricePerMinuteAccurate">Dollars per minute on the Accurate profile.</param>
    /// <param name="pricePerMinuteFast">Dollars per minute on the Fast profile.</param>
    /// <returns>The dashboard's figures.</returns>
    public static UsageSummary Summarise(
        IReadOnlyList<TranscriptRecord> records,
        int typingWordsPerMinute = DefaultTypingWordsPerMinute,
        double pricePerMinuteAccurate = DefaultPricePerMinuteAccurate,
        double pricePerMinuteFast = DefaultPricePerMinuteFast)
    {
        ArgumentNullException.ThrowIfNull(records);

        var wpm = typingWordsPerMinute >= 1 ? typingWordsPerMinute : DefaultTypingWordsPerMinute;
        var words = 0;
        var seconds = 0.0;
        var savedSeconds = 0.0;
        var cost = 0.0;
        var fellBack = 0;
        var engines = new Dictionary<TranscriberKind, EngineUsage>();

        foreach (var record in records)
        {
            var count = TranscriptStats.CountWords(record.Text);
            var audio = Math.Max(0, record.AudioSeconds);
            var minutes = audio / 60;

            words += count;
            seconds += audio;
            savedSeconds += (count * 60.0 / wpm) - audio - Math.Max(0, record.ProcessingSeconds);
            if (record.FellBack) fellBack++;

            if (record.Engine == TranscriberKind.Azure)
            {
                var price = record.Profile == CloudProfile.Fast ? pricePerMinuteFast : pricePerMinuteAccurate;
                cost += minutes * Math.Max(0, price);
            }

            var usage = engines.GetValueOrDefault(record.Engine, new EngineUsage(record.Engine, 0, 0, 0));
            engines[record.Engine] = usage with
            {
                Dictations = usage.Dictations + 1,
                Words = usage.Words + count,
                AudioMinutes = usage.AudioMinutes + minutes,
            };
        }

        return new UsageSummary(
            records.Count,
            words,
            seconds / 60,
            TimeSpan.FromSeconds(Math.Max(0, savedSeconds)),
            cost,
            fellBack,
            [.. engines.Values.OrderByDescending(e => e.AudioMinutes).ThenBy(e => e.Engine)]);
    }

    /// <summary>A duration the way the dashboard shows it: "45 s", "14 min", "2 h 5 min".</summary>
    /// <param name="duration">The duration.</param>
    /// <param name="culture">Number formatting.</param>
    /// <returns>The text.</returns>
    public static string FormatDuration(TimeSpan duration, IFormatProvider? culture = null)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return string.Format(culture, "{0:0} s", Math.Max(0, Math.Floor(duration.TotalSeconds)));
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return string.Format(culture, "{0:0} min", Math.Floor(duration.TotalMinutes));
        }

        return string.Format(culture, "{0:0} h {1:0} min", Math.Floor(duration.TotalHours), duration.Minutes);
    }

    /// <summary>An estimated cost: "$0.42", "&lt;$0.01", or "$0" for none at all.</summary>
    /// <param name="dollars">US dollars.</param>
    /// <param name="culture">Number formatting.</param>
    /// <returns>The text.</returns>
    public static string FormatCost(double dollars, IFormatProvider? culture = null) => dollars switch
    {
        <= 0 => "$0",
        < 0.01 => "<$0.01",
        _ => string.Format(culture, "${0:0.00}", dollars),
    };
}
