using System.Text;

namespace Murmur.Core;

/// <summary>The outcome of one filler-removal pass.</summary>
/// <param name="Text">The text to use — unchanged when nothing was removed.</param>
/// <param name="Removed">How many filler tokens were dropped.</param>
public readonly record struct FillerFilterResult(string Text, int Removed)
{
    /// <summary>Whether anything was removed.</summary>
    public bool WasChanged => Removed > 0;
}

/// <summary>
/// Drops the noises people make while thinking.
/// </summary>
/// <remarks>
/// <para>
/// "So, um, I think, uh, we should ship it" is what the microphone heard; "So I think we should
/// ship it" is what the user meant to write. The recogniser transcribes disfluencies faithfully
/// because that is its job, and removing them is text clean-up, not speech recognition.
/// </para>
/// <para>
/// <b>The strip list is tiny and closed, and matching is whole-token only.</b> That restriction
/// is the entire safety argument. Every entry here is a real word or name in some context —
/// "um" is a Southeast Asian surname, "ah" opens "ah-ha", "er" is inside "err", "Ahmed",
/// "German", "her", "there" — and the only reliable way to tell a disfluency from a word is
/// that a disfluency stands alone. Nothing here is ever matched as a substring, and a token
/// only counts if what remains after stripping its punctuation is exactly one of these.
/// </para>
/// <para>
/// Two further guards:
/// </para>
/// <list type="bullet">
/// <item><b>A hyphenated or apostrophised token is never a filler.</b> "ah-ha", "uh-huh" and
/// "um-hmm" all reduce to a filler if you only look at letters, and all three are words.</item>
/// <item><b>Punctuation is carried backwards.</b> Dropping the token in "Well, um, fine" must
/// not leave "Well, fine" missing a comma or, worse, "Well fine" — so the removed token's
/// trailing punctuation is transplanted onto the token before it when that token has none.</item>
/// </list>
/// <para>
/// This is not a hedge-word remover. "Like", "you know", "sort of", "basically" and "actually"
/// are deliberately absent: they carry meaning often enough that stripping them changes what
/// the user said, and "like" in particular is a preposition, a verb and a conjunction.
/// </para>
/// </remarks>
public static class FillerFilter
{
    /// <summary>
    /// The complete strip list.
    /// </summary>
    /// <remarks>
    /// Only sounds with no lexical meaning of their own in English. Each is matched as a whole
    /// token and nothing else.
    /// </remarks>
    public static readonly IReadOnlySet<string> Fillers = new HashSet<string>(StringComparer.Ordinal)
    {
        "um", "umm", "ummm",
        "uh", "uhh", "uhhh",
        "er", "err", "erm", "ers",
        "ah", "ahh", "ahhh",
        "eh", "ehh",
        "hm", "hmm", "hmmm",
        "mm", "mmm",
        "uhm", "ehm",
    };

    /// <summary>
    /// Tokens that look like a filler but are words, and are never removed.
    /// </summary>
    /// <remarks>
    /// Backstop rather than primary defence — the hyphen guard already catches most of these —
    /// but "uh huh" and "mm hmm" arrive from some recognisers as two separate tokens, and
    /// removing half of an agreement leaves a stray "huh".
    /// </remarks>
    public static readonly IReadOnlySet<string> Protected = new HashSet<string>(StringComparer.Ordinal)
    {
        "huh", "hmph", "ahem", "aha", "oh", "ohh", "ha",
    };

    /// <summary>Removes filler tokens from <paramref name="text"/>.</summary>
    /// <param name="text">Transcribed text.</param>
    /// <returns>The text to use, unchanged when there was nothing to remove.</returns>
    public static string Apply(string text) => Analyse(text).Text;

    /// <summary>Removes filler tokens and reports how many went.</summary>
    /// <param name="text">Transcribed text.</param>
    /// <returns>The result, including a count of zero when nothing matched.</returns>
    public static FillerFilterResult Analyse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new FillerFilterResult(text, 0);

        var tokens = SpeechText.Tokenize(text);
        var kept = new List<string>(tokens.Count);
        var removed = 0;
        var lostTheOpener = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (!IsFiller(token))
            {
                kept.Add(token.Raw);
                continue;
            }

            removed++;
            if (i == 0) lostTheOpener = true;

            // Carry the dropped token's punctuation back onto its predecessor, so "Well, um,
            // fine" becomes "Well, fine" rather than "Well, fine" losing a break or gaining a
            // doubled one. Only when the predecessor has none of its own — two commas in a row
            // is worse than one in the wrong place.
            var trailing = TrailingPunctuation(token.Raw);
            if (trailing.Length > 0 && kept.Count > 0)
            {
                var previous = kept[^1];
                if (TrailingPunctuation(previous).Length == 0) kept[^1] = previous + trailing;
            }
        }

        if (removed == 0) return new FillerFilterResult(text, 0);

        // Everything was filler. Returning an empty string here is correct and deliberate: the
        // engine already refuses to inject whitespace, so a recording of pure hesitation types
        // nothing rather than typing "um".
        var rebuilt = Rebuild(kept, lostTheOpener);
        return new FillerFilterResult(rebuilt, removed);
    }

    /// <summary>Whether one token is a disfluency rather than a word.</summary>
    /// <param name="token">The token to judge.</param>
    /// <returns>True only for a stand-alone filler sound.</returns>
    public static bool IsFiller(SpokenToken token)
    {
        var raw = token.Raw;
        if (raw.Length == 0) return false;

        // "ah-ha", "uh-huh", "mm-hmm": a joined token is a word, whatever its letters reduce to.
        foreach (var c in raw)
        {
            if (c is '-' or '\'' or '’' or '_' or '/') return false;
        }

        var word = token.Word;
        if (word.Length == 0) return false;

        return !Protected.Contains(word) && Fillers.Contains(word);
    }

    private static string TrailingPunctuation(string raw)
    {
        var end = raw.Length;
        while (end > 0 && !char.IsLetterOrDigit(raw[end - 1])) end--;

        return end >= raw.Length ? string.Empty : raw[end..];
    }

    /// <summary>
    /// Rejoins the survivors, tidying the punctuation the removals left behind.
    /// </summary>
    /// <remarks>
    /// Two artefacts are possible and both look like a bug rather than a clean-up: a sentence
    /// that now opens with a comma ("um, fine" → ", fine"), and a doubled break where the
    /// carried punctuation met existing punctuation.
    /// </remarks>
    private static string Rebuild(List<string> kept, bool lostTheOpener)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < kept.Count; i++)
        {
            var token = kept[i];

            // A leading orphan of punctuation is what is left when the very first word was the
            // filler and it carried nothing to give back.
            if (i == 0) token = token.TrimStart(',', ';', ':');

            if (token.Length == 0) continue;

            if (builder.Length > 0) builder.Append(' ');
            builder.Append(token);
        }

        var text = builder.ToString().Trim();

        // Only when the utterance's first word was the thing removed. Recasing a word the pass
        // never touched would be a mutation nobody asked for.
        return lostTheOpener ? SpeechText.Capitalize(text) : text;
    }
}
