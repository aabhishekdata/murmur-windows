using System.Globalization;

namespace Murmur.Core;

/// <summary>
/// One whitespace-delimited token of transcribed speech.
/// </summary>
/// <param name="Raw">Exactly as it appeared, punctuation and all.</param>
/// <param name="Word">Lowercased and stripped of leading and trailing punctuation.</param>
public readonly record struct SpokenToken(string Raw, string Word)
{
    /// <summary>Whether this token ends a clause — the recogniser heard a pause here.</summary>
    public bool EndsClause =>
        Raw.Length > 0 && Raw[^1] is '.' or ',' or ';' or ':' or '!' or '?';

    /// <summary>Whether this token ends a sentence outright.</summary>
    public bool EndsSentence =>
        Raw.Length > 0 && Raw[^1] is '.' or '!' or '?';

    /// <summary>Whether this token carries a comma, which is a softer break than a full stop.</summary>
    public bool EndsWithComma => Raw.Length > 0 && Raw[^1] is ',';
}

/// <summary>
/// Tokenising and the word lists every deterministic text pass shares.
/// </summary>
/// <remarks>
/// <para>
/// Five passes now rewrite the user's words before they are typed into somebody else's
/// application, and every one of them decides what to do by looking at the word in front of or
/// behind a match. Those judgements have to agree: a word that proves "one" is a quantity
/// rather than a list marker proves the same thing about the "no" in "actually no". Keeping one
/// copy of the lists is what stops the passes drifting apart.
/// </para>
/// <para>
/// Everything here is invariant-culture and ordinal. <c>ToLowerInvariant</c> rather than the
/// current culture, because in Turkish <c>"I".ToLower()</c> is <c>"ı"</c> and an ASCII-keyed
/// table would silently stop matching — the same reason the dictionary corrector is invariant.
/// </para>
/// </remarks>
public static class SpeechText
{
    /// <summary>Characters that separate tokens.</summary>
    public static readonly char[] Whitespace = [' ', '\t', '\n', '\r'];

    /// <summary>Splits text into tokens, dropping empty runs.</summary>
    /// <param name="text">Transcribed text.</param>
    /// <returns>One token per whitespace-delimited run.</returns>
    public static List<SpokenToken> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var raw = text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        var tokens = new List<SpokenToken>(raw.Length);

        foreach (var piece in raw) tokens.Add(new SpokenToken(piece, BareWord(piece)));

        return tokens;
    }

    /// <summary>Lowercases a token and strips the punctuation around it.</summary>
    /// <param name="token">One raw token.</param>
    /// <returns>The bare word, or empty when the token is punctuation only.</returns>
    public static string BareWord(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var start = 0;
        var end = token.Length;

        while (start < end && !char.IsLetterOrDigit(token[start])) start++;
        while (end > start && !char.IsLetterOrDigit(token[end - 1])) end--;

        return start >= end ? string.Empty : token[start..end].ToLowerInvariant();
    }

    /// <summary>Rejoins a run of tokens, preserving their original spelling.</summary>
    /// <param name="tokens">The token list.</param>
    /// <param name="from">First index, inclusive.</param>
    /// <param name="to">Last index, exclusive.</param>
    /// <returns>The joined text, trimmed.</returns>
    public static string Join(List<SpokenToken> tokens, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (to <= from) return string.Empty;

        from = Math.Max(0, from);
        to = Math.Min(tokens.Count, to);
        if (to <= from) return string.Empty;

        return string.Join(' ', tokens.GetRange(from, to - from).Select(t => t.Raw)).Trim();
    }

    /// <summary>
    /// Upper-cases the first letter, unless doing so would wreck a name.
    /// </summary>
    /// <param name="text">The text to capitalise.</param>
    /// <returns>
    /// The text with a capital first letter. "apples" becomes "Apples", but "iPhone" is left
    /// alone — a lowercase first letter followed by an uppercase second letter is a brand, not
    /// a sentence that needs starting.
    /// </returns>
    public static string Capitalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0 || !char.IsLower(text[0])) return text;
        if (text.Length > 1 && char.IsUpper(text[1])) return text;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{char.ToUpperInvariant(text[0])}{text[1..]}");
    }

    /// <summary>
    /// Words that, sitting directly in front of a match, prove it is ordinary prose.
    /// </summary>
    /// <remarks>
    /// Determiners ("the first time"), prepositions ("one of two"), auxiliaries and the common
    /// transitive verbs that take a counted object ("I need one apple"). This is the single
    /// most load-bearing guard in the text passes: nearly every plausible false positive is a
    /// trigger word preceded by one of these.
    /// </remarks>
    public static readonly IReadOnlySet<string> BlockedBefore = new HashSet<string>(StringComparer.Ordinal)
    {
        // Determiners and possessives.
        "the", "a", "an", "this", "that", "these", "those", "my", "your", "his", "her", "its",
        "our", "their", "every", "each", "any", "some", "no", "another", "other", "such",
        // Prepositions and conjunctions.
        "of", "in", "on", "at", "for", "to", "from", "with", "by", "about", "into", "onto",
        "over", "under", "after", "before", "since", "until", "than", "and", "or", "but",
        "nor", "as", "like", "per", "via", "within", "without", "between", "among",
        // Auxiliaries and copulas.
        "is", "was", "are", "were", "am", "be", "been", "being", "has", "have", "had",
        "do", "does", "did", "will", "would", "can", "could", "shall", "should", "may",
        "might", "must",
        // Verbs that habitually take a counted object.
        "need", "needs", "needed", "want", "wants", "wanted", "get", "gets", "got", "buy",
        "buys", "bought", "take", "takes", "took", "see", "sees", "saw", "add", "adds",
        "added", "make", "makes", "made", "find", "finds", "found", "give", "gives", "gave",
        "pick", "picks", "picked", "choose", "chose", "eat", "ate", "order", "ordered",
        "count", "counted", "say", "says", "said", "know", "knows", "knew", "think",
        "thinks", "thought", "use", "uses", "used",
        // Degree words.
        "only", "just", "least", "most", "very", "really", "almost", "nearly",
        "still", "even", "also", "again",
    };

    /// <summary>
    /// Words that cannot begin a rewritten fragment — a list item or a spoken replacement.
    /// </summary>
    /// <remarks>
    /// A real enumeration marker or correction marker is followed by the thing itself, never by
    /// a dangling preposition. This is what protects "First of all, thanks" and "I meant to
    /// call you".
    /// </remarks>
    public static readonly IReadOnlySet<string> BlockedFragmentStart = new HashSet<string>(StringComparer.Ordinal)
    {
        "of", "and", "or", "but", "nor", "so", "to", "the", "a", "an", "in", "on", "at",
        "for", "with", "from", "by", "that", "which", "who", "whom", "whose", "than",
        "because", "though", "although", "while",
    };
}
