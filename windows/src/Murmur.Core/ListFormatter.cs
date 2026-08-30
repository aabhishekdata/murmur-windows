using System.Globalization;
using System.Text;

namespace Murmur.Core;

/// <summary>Which shape of list a piece of text was rewritten into.</summary>
public enum ListStyle
{
    /// <summary>Nothing was rewritten.</summary>
    None,

    /// <summary>A spoken enumeration became a numbered list.</summary>
    Numbered,

    /// <summary>A bare comma-separated run of short items became a bulleted list.</summary>
    Bulleted,
}

/// <summary>The outcome of one formatting attempt.</summary>
/// <param name="Text">The text to use — unchanged when nothing matched.</param>
/// <param name="Style">Which rewrite fired, if any.</param>
/// <param name="ItemCount">How many list items were produced. Zero when nothing matched.</param>
public readonly record struct ListFormatResult(string Text, ListStyle Style, int ItemCount)
{
    /// <summary>Whether the text was rewritten.</summary>
    public bool WasFormatted => Style != ListStyle.None;
}

/// <summary>
/// Turns a spoken enumeration into a real, line-broken list.
/// </summary>
/// <remarks>
/// <para>
/// Dictating "one apples two bananas three oranges" produces one flat sentence, because the
/// recogniser has no idea the speaker was counting. This pass reads the transcript back and
/// rewrites genuine enumerations into
/// <c>1. Apples\n2. Bananas\n3. Oranges</c>, which is the behaviour Wispr Flow documents
/// ("just speak the numbers").
/// </para>
/// <para>
/// <b>There is no model here and there must not be one.</b> Murmur's pipeline is ASR plus
/// deterministic text passes, so this is pure pattern matching — the same input always gives
/// the same output, and it can be exhaustively unit-tested.
/// </para>
/// <para>
/// <b>The design bias is toward doing nothing.</b> Mangling ordinary prose that happens to
/// contain "first" or "one" is far worse than missing a list, so every rule below is a reason
/// to bail out:
/// </para>
/// <list type="number">
/// <item><b>Two or more markers, in sequence, starting at one.</b> A lone "one" is a quantity,
/// not a list. "Two … one" is not counting.</item>
/// <item><b>The first marker needs structural support</b> — it starts the text, follows
/// sentence punctuation, or carries its own ("One," / "1."). This is what separates
/// "One, milk. Two, eggs." from "I need one apple".</item>
/// <item><b>No marker may sit behind a determiner, preposition or auxiliary.</b> "the first
/// time", "one of two", "I have one question" all die here.</item>
/// <item><b>Items must look like items</b> — short, and not opening with a function word, which
/// is what keeps "First of all … Second of all …" intact.</item>
/// </list>
/// <para>
/// A second, much narrower pass handles the bare "milk, eggs, bread, butter" case: only when
/// the <i>entire</i> utterance is three or more one-to-three-word noun-ish fragments with no
/// verbs and no other sentence punctuation.
/// </para>
/// </remarks>
public static class ListFormatter
{
    /// <summary>
    /// Longest an item may be before the run stops looking like a list.
    /// </summary>
    /// <remarks>
    /// Real spoken list items are short. A long stretch between two number words is prose that
    /// happens to contain numbers, which is exactly what must not be touched.
    /// </remarks>
    public const int MaxItemWords = 12;

    /// <summary>Smallest run of markers that counts as an enumeration.</summary>
    public const int MinListItems = 2;

    /// <summary>Smallest number of fragments the bare comma-list pass will accept.</summary>
    public const int MinCommaListItems = 3;

    /// <summary>Longest a comma-separated fragment may be to still read as a list item.</summary>
    /// <remarks>
    /// Widened from three to four. Four still cannot hold a clause — a clause needs a verb or a
    /// pronoun and both are refused outright — but it does hold the compound nouns people
    /// actually dictate: "extra virgin olive oil", "large red onions".
    /// </remarks>
    public const int MaxCommaItemWords = 4;

    /// <summary>Highest enumeration value recognised. Beyond this it is a quantity, not a list.</summary>
    public const int MaxMarkerValue = 20;

    /// <summary>Words above which a trailing full stop is prose punctuation, not list noise.</summary>
    private const int ShortItemWords = 3;

    /// <summary>Longest lead-in that may introduce a colon list.</summary>
    /// <remarks>
    /// "Here's what I need:" announces a list. A paragraph ending in a colon is prose that
    /// happens to contain one.
    /// </remarks>
    private const int MaxLeadInWords = 8;

    private static readonly char[] Whitespace = [' ', '\t', '\n', '\r'];

    private static readonly char[] SentenceMarks = ['.', '!', '?', ';', ':'];

    private static readonly string[] TrailingConjunctions = ["and ", "or ", "plus "];

    private static readonly string[] CardinalWords =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen",
        "eighteen", "nineteen", "twenty",
    ];

    private static readonly string[] OrdinalWords =
    [
        "zeroth", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth",
        "ninth", "tenth", "eleventh", "twelfth", "thirteenth", "fourteenth", "fifteenth",
        "sixteenth", "seventeenth", "eighteenth", "nineteenth", "twentieth",
    ];

    /// <summary>
    /// Words that, sitting directly in front of a number, prove it is a quantity or a
    /// modifier rather than an enumeration marker.
    /// </summary>
    /// <remarks>
    /// Determiners ("the first time"), prepositions ("one of two"), auxiliaries and common
    /// transitive verbs ("I have one question", "I need one apple"). This list is the single
    /// most load-bearing guard in the file: nearly every plausible false positive is a number
    /// preceded by one of these.
    /// </remarks>
    private static readonly HashSet<string> BlockedBefore = new(StringComparer.Ordinal)
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
        "only", "just", "least", "most", "very", "really", "almost", "about", "nearly",
        "still", "even", "also", "again",
    };

    /// <summary>
    /// Words that cannot begin a list item.
    /// </summary>
    /// <remarks>
    /// This is what protects "First of all, thanks. Second of all, sorry." — a real
    /// enumeration marker is followed by the item itself, never by a dangling preposition.
    /// </remarks>
    private static readonly HashSet<string> BlockedItemStart = new(StringComparer.Ordinal)
    {
        "of", "and", "or", "but", "nor", "so", "to", "the", "a", "an", "in", "on", "at",
        "for", "with", "from", "by", "that", "which", "who", "whom", "whose", "than",
        "because", "though", "although", "while",
    };

    /// <summary>Verbs and auxiliaries that stop a comma fragment reading as a list item.</summary>
    private static readonly HashSet<string> CommaItemVerbs = new(StringComparer.Ordinal)
    {
        "is", "was", "are", "were", "am", "be", "been", "being", "has", "have", "had",
        "do", "does", "did", "will", "would", "can", "could", "shall", "should", "may",
        "might", "must", "went", "got", "said", "came", "made", "took", "saw", "knew",
        "thought", "felt", "seemed", "looked", "wanted", "needed",
    };

    /// <summary>
    /// Discourse markers and pronouns that make a comma fragment part of a sentence.
    /// </summary>
    /// <remarks>
    /// "Yeah, sure, okay" and "It, he, she" are three comma-separated one-word fragments, and
    /// without this they would be bulleted.
    /// </remarks>
    private static readonly HashSet<string> CommaItemStopWords = new(StringComparer.Ordinal)
    {
        "yeah", "yes", "no", "ok", "okay", "sure", "well", "um", "uh", "er", "hmm", "right",
        "thanks", "hello", "hi", "hey", "bye", "please", "actually", "anyway", "however",
        "therefore", "then", "finally", "again", "also", "too", "just", "really", "very",
        "i", "you", "he", "she", "it", "we", "they", "me", "him", "us", "them", "who",
        "what", "when", "where", "why", "how", "and", "or", "but", "so", "the", "a", "an",
        "of", "in", "on", "at", "to", "for", "with", "from", "by", "that", "this",

        // Bare numbers. "fifty, sixty, seventy" is counting out loud, not a shopping list,
        // and a list whose every item is a number is far more likely to be a quantity.
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen",
        "eighteen", "nineteen", "twenty", "thirty", "forty", "fifty", "sixty", "seventy",
        "eighty", "ninety", "hundred", "thousand", "million",
    };

    /// <summary>Which family a marker belongs to. Runs never mix the two.</summary>
    private enum MarkerFamily
    {
        /// <summary>"one", "1", "number one".</summary>
        Cardinal,

        /// <summary>"first", "1st".</summary>
        Ordinal,
    }

    /// <summary>One recognised enumeration marker.</summary>
    /// <param name="Start">Index of the marker's first token.</param>
    /// <param name="End">Index one past the marker's last token.</param>
    /// <param name="Value">The number it names.</param>
    /// <param name="Family">Cardinal or ordinal.</param>
    /// <param name="HasOwnPunctuation">Whether the marker carries trailing punctuation ("One,").</param>
    private sealed record Marker(int Start, int End, int Value, MarkerFamily Family, bool HasOwnPunctuation);

    /// <summary>One whitespace-delimited token, with its bare word form.</summary>
    /// <param name="Raw">Exactly as it appeared.</param>
    /// <param name="Word">Lowercased, stripped of leading and trailing punctuation.</param>
    private sealed record Token(string Raw, string Word)
    {
        /// <summary>Whether this token ends a clause — the recogniser heard a pause here.</summary>
        public bool EndsClause =>
            Raw.Length > 0 && Raw[^1] is '.' or ',' or ';' or ':' or '!' or '?';

        /// <summary>Whether this token ends a sentence outright.</summary>
        public bool EndsSentence =>
            Raw.Length > 0 && Raw[^1] is '.' or '!' or '?';
    }

    /// <summary>
    /// Rewrites <paramref name="text"/> as a list when it clearly is one.
    /// </summary>
    /// <param name="text">Transcribed text, after the dictionary correction pass.</param>
    /// <returns>The text to use, unchanged unless a list was recognised.</returns>
    public static string Format(string text) => Analyse(text).Text;

    /// <summary>
    /// Rewrites <paramref name="text"/> and reports which rule fired.
    /// </summary>
    /// <param name="text">Transcribed text, after the dictionary correction pass.</param>
    /// <returns>The result, including <see cref="ListStyle.None"/> when nothing matched.</returns>
    public static ListFormatResult Analyse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new ListFormatResult(text, ListStyle.None, 0);

        // Already multi-line: the user (or an earlier pass) has structured this, so leave it be.
        if (text.Contains('\n', StringComparison.Ordinal))
        {
            return new ListFormatResult(text, ListStyle.None, 0);
        }

        var numbered = TryFormatEnumeration(text);
        if (numbered.WasFormatted) return numbered;

        return TryFormatCommaList(text);
    }

    // ---- Spoken enumeration: "one … two … three", "first … second", "1. … 2." ----

    private static ListFormatResult TryFormatEnumeration(string text)
    {
        var tokens = Tokenize(text);
        if (tokens.Count < 3) return new ListFormatResult(text, ListStyle.None, 0);

        var markers = FindMarkers(tokens);
        if (markers.Count < MinListItems) return new ListFormatResult(text, ListStyle.None, 0);

        // Try every "one"/"first" as a possible start; take the first run that survives every
        // guard. Starting anywhere else is not counting.
        foreach (var start in markers.Where(m => m.Value == 1))
        {
            var run = BuildRun(markers, start);
            if (run.Count < MinListItems) continue;

            var rendered = Render(tokens, run);
            if (rendered is not null) return new ListFormatResult(rendered, ListStyle.Numbered, run.Count);
        }

        return new ListFormatResult(text, ListStyle.None, 0);
    }

    /// <summary>Greedily extends a run: 1, then the next 2, then the next 3, all same family.</summary>
    private static List<Marker> BuildRun(List<Marker> markers, Marker start)
    {
        var run = new List<Marker> { start };

        while (true)
        {
            var last = run[^1];
            var next = markers.FirstOrDefault(m =>
                m.Start >= last.End &&
                m.Value == last.Value + 1 &&
                m.Family == last.Family);

            if (next is null) return run;
            run.Add(next);
        }
    }

    /// <summary>
    /// Applies every conservatism rule and, if they all pass, produces the rewritten text.
    /// </summary>
    /// <returns>The formatted text, or null when this run is not a list after all.</returns>
    private static string? Render(List<Token> tokens, List<Marker> run)
    {
        // Rule 2: the run has to begin somewhere a list could begin.
        if (!HasStructuralSupport(tokens, run[0])) return null;

        // Rule 3: no marker may be sitting behind a determiner, preposition or auxiliary.
        foreach (var marker in run)
        {
            if (marker.Start == 0) continue;

            var previous = tokens[marker.Start - 1];
            if (previous.EndsClause) continue;
            if (BlockedBefore.Contains(previous.Word)) return null;
        }

        var items = new List<string>(run.Count);

        // Every item except the last is bounded by the following marker.
        for (var i = 0; i < run.Count - 1; i++)
        {
            var item = Slice(tokens, run[i].End, run[i + 1].Start);
            if (!IsUsableItem(item, tokens, run[i].End)) return null;
            items.Add(item);
        }

        // The last item runs to the end of the utterance — unless a sentence ends inside it
        // with more text after, in which case that text is prose the list must not swallow.
        var tailStart = run[^1].End;
        var cut = FindSentenceCut(tokens, tailStart);

        var lastItem = Slice(tokens, tailStart, cut);
        if (!IsUsableItem(lastItem, tokens, tailStart)) return null;
        items.Add(lastItem);

        var builder = new StringBuilder();

        var preamble = Slice(tokens, 0, run[0].Start);
        if (preamble.Length > 0) builder.Append(preamble).Append('\n');

        for (var i = 0; i < items.Count; i++)
        {
            builder.Append(i + 1).Append(". ").Append(CleanItem(items[i], stripTrailingStop: true));
            if (i < items.Count - 1) builder.Append('\n');
        }

        var trailing = Slice(tokens, cut, tokens.Count);
        if (trailing.Length > 0) builder.Append('\n').Append(trailing);

        return builder.ToString();
    }

    /// <summary>
    /// Whether the first marker sits somewhere an enumeration could plausibly start.
    /// </summary>
    /// <remarks>
    /// Start of the utterance, immediately after sentence punctuation, or carrying its own
    /// ("One," / "1."). Without this, "I need one apple two oranges" — a quantity followed by
    /// another quantity — reads as a list.
    /// </remarks>
    private static bool HasStructuralSupport(List<Token> tokens, Marker marker)
    {
        if (marker.Start == 0) return true;
        if (marker.HasOwnPunctuation) return true;
        return tokens[marker.Start - 1].EndsClause;
    }

    /// <summary>Whether the text between two markers looks like a list item.</summary>
    private static bool IsUsableItem(string item, List<Token> tokens, int firstTokenIndex)
    {
        if (item.Length == 0) return false;

        var words = item.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > MaxItemWords) return false;

        // Rule 4: "First of all …" is not item one of a list.
        if (firstTokenIndex < tokens.Count && BlockedItemStart.Contains(tokens[firstTokenIndex].Word))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Finds where the final item stops.
    /// </summary>
    /// <remarks>
    /// A sentence ending mid-tail means the list finished and ordinary prose resumed
    /// ("… two eggs. Then I went home."). A sentence ending on the very last token is just how
    /// the utterance was punctuated and belongs to the item.
    /// </remarks>
    private static int FindSentenceCut(List<Token> tokens, int from)
    {
        for (var i = from; i < tokens.Count - 1; i++)
        {
            if (tokens[i].EndsSentence) return i + 1;
        }

        return tokens.Count;
    }

    // ---- Bare comma list: "milk, eggs, bread, butter" ----

    private static ListFormatResult TryFormatCommaList(string text)
    {
        var none = new ListFormatResult(text, ListStyle.None, 0);

        var trimmed = text.Trim();
        if (trimmed.Length == 0) return none;

        // One closing full stop is normal dictation; anything else is sentence structure and
        // this pass has no business touching it.
        if (trimmed.EndsWith('.') || trimmed.EndsWith('!')) trimmed = trimmed[..^1].TrimEnd();

        // A colon is the one piece of sentence structure that argues *for* a list rather than
        // against one: "Here's what I need: milk, eggs, bread" is a person announcing a list out
        // loud. Everything before the colon is kept verbatim as a lead-in, exactly as the
        // numbered pass keeps a preamble; everything after it goes through the same fragment
        // tests as a bare list. Only the first colon counts — a second one is prose.
        var lead = string.Empty;
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);

        if (colon >= 0)
        {
            lead = trimmed[..colon].Trim();
            trimmed = trimmed[(colon + 1)..].Trim();

            // A bare or run-on lead-in is not an announcement. Requiring real words in front of
            // the colon keeps ":" from acting as a licence on arbitrary text.
            if (lead.Length == 0 || trimmed.Length == 0) return none;
            if (lead.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries).Length > MaxLeadInWords)
            {
                return none;
            }
        }

        if (trimmed.IndexOfAny(SentenceMarks) >= 0) return none;
        if (!trimmed.Contains(',', StringComparison.Ordinal)) return none;

        var parts = trimmed
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (parts.Count < MinCommaListItems) return none;

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];

            // "… , and butter" — the spoken conjunction belongs to the list, not the item.
            if (i == parts.Count - 1)
            {
                part = StripLeadingConjunction(part);
                parts[i] = part;
            }

            if (!IsUsableCommaItem(part)) return none;
        }

        var builder = new StringBuilder();
        if (lead.Length > 0) builder.Append(lead).Append(':').Append('\n');

        for (var i = 0; i < parts.Count; i++)
        {
            builder.Append("- ").Append(CleanItem(parts[i], stripTrailingStop: true));
            if (i < parts.Count - 1) builder.Append('\n');
        }

        return new ListFormatResult(builder.ToString(), ListStyle.Bulleted, parts.Count);
    }

    private static string StripLeadingConjunction(string part)
    {
        foreach (var conjunction in TrailingConjunctions)
        {
            if (part.StartsWith(conjunction, StringComparison.OrdinalIgnoreCase))
            {
                return part[conjunction.Length..].Trim();
            }
        }

        return part;
    }

    private static bool IsUsableCommaItem(string part)
    {
        if (part.Length < 2) return false;

        var words = part.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > MaxCommaItemWords) return false;

        foreach (var word in words)
        {
            var bare = BareWord(word);
            if (bare.Length == 0) return false;

            // A fragment with a verb in it is a clause, and clauses are not list items.
            if (CommaItemVerbs.Contains(bare)) return false;
            if (CommaItemStopWords.Contains(bare)) return false;

            // Anything carrying punctuation of its own is sentence structure.
            if (!IsPlainWord(word)) return false;
        }

        return true;
    }

    private static bool IsPlainWord(string word)
    {
        foreach (var c in word)
        {
            if (!char.IsLetterOrDigit(c) && c is not '-' and not '\'' and not '’') return false;
        }

        return word.Length > 0;
    }

    // ---- Shared helpers ----

    private static List<Token> Tokenize(string text)
    {
        var raw = text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        var tokens = new List<Token>(raw.Length);

        foreach (var piece in raw) tokens.Add(new Token(piece, BareWord(piece)));

        return tokens;
    }

    /// <summary>Lowercases and strips surrounding punctuation. Invariant, deliberately.</summary>
    /// <remarks>
    /// <c>ToLowerInvariant</c> rather than the current culture: in Turkish, "I".ToLower() is
    /// "ı", and a marker table keyed on ASCII words would then stop matching. Same reason the
    /// dictionary corrector is invariant.
    /// </remarks>
    private static string BareWord(string token)
    {
        var start = 0;
        var end = token.Length;

        while (start < end && !char.IsLetterOrDigit(token[start])) start++;
        while (end > start && !char.IsLetterOrDigit(token[end - 1])) end--;

        return start >= end ? string.Empty : token[start..end].ToLowerInvariant();
    }

    private static string Slice(List<Token> tokens, int from, int to)
    {
        if (to <= from) return string.Empty;
        return string.Join(' ', tokens.GetRange(from, to - from).Select(t => t.Raw)).Trim();
    }

    /// <summary>Tidies one item: drops the punctuation that joined it to its neighbours.</summary>
    /// <remarks>
    /// A trailing full stop is only removed from a short item. "Apples." was a list entry the
    /// recogniser punctuated; "I didn't want to hurt her." is a sentence and keeps its stop.
    /// </remarks>
    private static string CleanItem(string item, bool stripTrailingStop)
    {
        var text = item.Trim();
        if (text.Length == 0) return text;

        while (text.Length > 0 && (text[0] == ',' || text[0] == ':' || text[0] == ';'))
        {
            text = text[1..].TrimStart();
        }

        if (text.EndsWith(',')) text = text[..^1].TrimEnd();

        if (stripTrailingStop && text.EndsWith('.'))
        {
            var words = text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries).Length;
            if (words <= ShortItemWords) text = text[..^1].TrimEnd();
        }

        return Capitalize(text);
    }

    /// <summary>
    /// Upper-cases the first letter, unless doing so would wreck a name.
    /// </summary>
    /// <remarks>
    /// "apples" becomes "Apples", but "iPhone" is left alone — a lowercase first letter
    /// followed by an uppercase second letter is a brand, not a sentence that needs starting.
    /// </remarks>
    private static string Capitalize(string text)
    {
        if (text.Length == 0 || !char.IsLower(text[0])) return text;
        if (text.Length > 1 && char.IsUpper(text[1])) return text;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{char.ToUpperInvariant(text[0])}{text[1..]}");
    }

    // ---- Marker recognition ----

    private static List<Marker> FindMarkers(List<Token> tokens)
    {
        var markers = new List<Marker>();

        for (var i = 0; i < tokens.Count; i++)
        {
            // "number one" / "number 1" — an explicit enumerator, two tokens wide.
            if (string.Equals(tokens[i].Word, "number", StringComparison.Ordinal) &&
                i + 1 < tokens.Count &&
                Cardinal(tokens[i + 1].Word) is { } numbered)
            {
                markers.Add(new Marker(i, i + 2, numbered, MarkerFamily.Cardinal, tokens[i + 1].EndsClause));
                i++;
                continue;
            }

            if (Cardinal(tokens[i].Word) is { } cardinal)
            {
                markers.Add(new Marker(i, i + 1, cardinal, MarkerFamily.Cardinal, tokens[i].EndsClause));
                continue;
            }

            if (Ordinal(tokens[i].Word) is { } ordinal)
            {
                markers.Add(new Marker(i, i + 1, ordinal, MarkerFamily.Ordinal, tokens[i].EndsClause));
            }
        }

        return markers;
    }

    /// <summary>"one" or "1" — the value, or null if this is not a cardinal in range.</summary>
    private static int? Cardinal(string word)
    {
        if (word.Length == 0) return null;

        var index = Array.IndexOf(CardinalWords, word);
        if (index >= 1) return index;

        if (int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var digits) &&
            digits is >= 1 and <= MaxMarkerValue)
        {
            return digits;
        }

        return null;
    }

    /// <summary>"first" or "1st" — the value, or null if this is not an ordinal in range.</summary>
    private static int? Ordinal(string word)
    {
        if (word.Length == 0) return null;

        var index = Array.IndexOf(OrdinalWords, word);
        if (index >= 1) return index;

        // "1st", "2nd", "3rd", "4th" — the digit forms a recogniser sometimes emits.
        if (word.Length < 3) return null;

        var suffix = word[^2..];
        if (suffix is not ("st" or "nd" or "rd" or "th")) return null;

        if (int.TryParse(word[..^2], NumberStyles.None, CultureInfo.InvariantCulture, out var digits) &&
            digits is >= 1 and <= MaxMarkerValue)
        {
            return digits;
        }

        return null;
    }
}
