using System.Globalization;
using System.Text;

namespace Murmur.E2E.Tests;

/// <summary>Word error rate: how far a transcript is from what was said, in words.</summary>
/// <remarks>
/// Speech recognition is not exact, so the suite asks for "close enough" rather than equality.
/// Both sides are lowercased and stripped of punctuation first, so "Hello, world." and
/// "hello world" count as the same two words.
/// </remarks>
internal static class WordErrorRate
{
    /// <summary>The most a clip's transcript may differ from what was said and still pass.</summary>
    public const double Tolerance = 0.25;

    /// <summary>Lowercased words with punctuation removed.</summary>
    /// <param name="text">Any text.</param>
    /// <returns>The words.</returns>
    public static string[] Words(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLower(CultureInfo.InvariantCulture))
        {
            // An apostrophe is dropped rather than split on, so "it's" and "its" compare equal.
            if (c == '\'' || c == '’') continue;
            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>(substitutions + deletions + insertions) / words said.</summary>
    /// <param name="said">The reference: what was spoken.</param>
    /// <param name="heard">The hypothesis: what was transcribed.</param>
    /// <returns>0 for a perfect transcript; can exceed 1 when much was inserted.</returns>
    public static double Of(string said, string heard)
    {
        var reference = Words(said);
        var hypothesis = Words(heard);
        if (reference.Length == 0) return hypothesis.Length == 0 ? 0 : 1;

        // Levenshtein distance over words, one row at a time.
        var previous = new int[hypothesis.Length + 1];
        var current = new int[hypothesis.Length + 1];
        for (var j = 0; j <= hypothesis.Length; j++) previous[j] = j;

        for (var i = 1; i <= reference.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= hypothesis.Length; j++)
            {
                var substitution = previous[j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }

            (previous, current) = (current, previous);
        }

        return (double)previous[hypothesis.Length] / reference.Length;
    }
}
