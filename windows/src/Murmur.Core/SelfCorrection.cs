namespace Murmur.Core;

/// <summary>The outcome of one self-correction pass.</summary>
/// <param name="Text">The text to use — unchanged when nothing was retracted.</param>
/// <param name="Applied">How many retractions fired.</param>
public readonly record struct SelfCorrectionResult(string Text, int Applied)
{
    /// <summary>Whether anything was retracted.</summary>
    public bool WasChanged => Applied > 0;
}

/// <summary>
/// Honours a spoken retraction: "Let's meet Tuesday, wait no, Friday" becomes
/// "Let's meet Friday".
/// </summary>
/// <remarks>
/// <para>
/// People correct themselves out loud constantly, and a recogniser writes both halves down.
/// This pass finds the small number of phrasings that unambiguously mean "ignore what I just
/// said" and removes the retracted words along with the marker.
/// </para>
/// <para>
/// <b>Deterministic, and deliberately much narrower than a model would be.</b> A model can
/// tell "wait, no, Friday" from "I can't wait, no really"; rules cannot, so the rules only fire
/// on shapes where the two cannot be confused. Everything below is a reason to do nothing,
/// because a false positive here <i>deletes words the user said</i> — the worst failure any of
/// these passes can produce, and one the user cannot see happen because the text has already
/// been typed into somebody else's application.
/// </para>
/// <para><b>How much gets deleted: symmetric substitution.</b></para>
/// <para>
/// The replacement is the clause after the marker. Whatever its length in words, exactly that
/// many words are removed from in front of the marker. People restate the same shape when they
/// correct themselves — "Tuesday" for "Friday", "at three on Tuesday" for "at four on Friday" —
/// so matching the lengths deletes the right span without needing to parse anything. It also
/// bounds the damage: a rule that can only ever remove as many words as it adds cannot eat a
/// sentence.
/// </para>
/// <para><b>The five guards, each of which alone cancels the rewrite:</b></para>
/// <list type="number">
/// <item><b>The marker must not straddle punctuation.</b> "I can't wait, no really" contains
/// the words "wait no" with a comma between them, and is not a retraction.</item>
/// <item><b>The retracted span must fit inside the clause in front of the marker.</b> It may
/// not reach back across a comma or a full stop into a previous thought. This is what stops
/// "Well, I mean, it depends" deleting "Well".</item>
/// <item><b>There must be a replacement, and it must look like content.</b> A marker at the end
/// of an utterance retracts nothing; one followed by a preposition or a conjunction — "I meant
/// to call you" — is ordinary prose, unless the retracted span opens with the same word, which
/// is what a parallel restatement looks like: "at three on Tuesday, wait no, at four on
/// Friday".</item>
/// <item><b>Weak markers need a clause boundary in front of them.</b> "I mean" and "I meant"
/// occur inside perfectly ordinary sentences ("do you know what I mean, Dave?"), and only
/// count when the speaker paused first.</item>
/// <item><b>Per-marker traps.</b> "actually no" is refused when the next word turns the "no"
/// into a quantifier — "actually no one told me" is not a correction.</item>
/// </list>
/// <para>
/// <b>"no wait" was considered and dropped.</b> It is genuinely ambiguous — "I said no, wait
/// for me" has the same token sequence as a retraction, with a comma in the same place a real
/// retraction would put one, and no positional guard separates them. Missing a correction costs
/// the user an edit; a false one silently deletes their words.
/// </para>
/// </remarks>
public static class SelfCorrection
{
    /// <summary>
    /// Longest replacement, in words, that will be treated as a restatement.
    /// </summary>
    /// <remarks>
    /// Beyond this the speaker is not restating a phrase, they are saying something new, and
    /// symmetric substitution would delete an unrelated span of the same length.
    /// </remarks>
    public const int MaxReplacementWords = 6;

    private enum MarkerStrength
    {
        /// <summary>Unambiguous enough to fire mid-clause.</summary>
        Strong,

        /// <summary>Occurs in ordinary prose; needs a pause in front of it.</summary>
        Weak,
    }

    /// <param name="Words">The bare words of the phrase, in order.</param>
    /// <param name="Strength">Whether the phrase needs a pause in front of it.</param>
    /// <param name="Traps">Words after the phrase that prove it is not a retraction.</param>
    /// <param name="RetractsSentence">
    /// True for "scratch that", which throws away the whole preceding sentence rather than a
    /// restated phrase.
    /// </param>
    private sealed record Marker(string[] Words, MarkerStrength Strength, string[] Traps, bool RetractsSentence = false)
    {
        /// <summary>Tokens after which a comma is part of the phrase rather than a break.</summary>
        public static readonly HashSet<string> CommaTolerant =
            new(StringComparer.Ordinal) { "sorry", "okay", "ok" };
    }

    /// <summary>
    /// Every recognised retraction phrase.
    /// </summary>
    /// <remarks>
    /// Longest first, so "sorry i meant" is matched before the "i meant" inside it.
    /// </remarks>
    private static readonly Marker[] Markers =
    [
        new(["sorry", "i", "meant"], MarkerStrength.Strong, []),
        new(["scratch", "that"], MarkerStrength.Strong, ["itch", "one", "off"], RetractsSentence: true),
        new(["actually", "no"], MarkerStrength.Strong,
            ["one", "longer", "more", "way", "idea", "doubt", "matter", "problem", "such"]),
        new(["wait", "no"], MarkerStrength.Strong,
            ["one", "longer", "more", "way", "idea", "doubt", "matter", "problem", "such"]),
        new(["i", "meant"], MarkerStrength.Weak, []),
        new(["i", "mean"], MarkerStrength.Weak, []),
    ];

    /// <summary>Applies every retraction found in <paramref name="text"/>.</summary>
    /// <param name="text">Transcribed text.</param>
    /// <returns>The text to use, unchanged when nothing matched.</returns>
    public static string Apply(string text) => Analyse(text).Text;

    /// <summary>Applies retractions and reports how many fired.</summary>
    /// <param name="text">Transcribed text.</param>
    /// <returns>The result, with a count of zero when nothing matched.</returns>
    public static SelfCorrectionResult Analyse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new SelfCorrectionResult(text, 0);

        var current = text;
        var applied = 0;

        // Re-run after each rewrite: "Tuesday, wait no, Thursday, wait no, Friday" is two
        // retractions, and the second one's clause only exists once the first has been applied.
        // Bounded so a rule that somehow failed to shorten the text cannot spin.
        for (var pass = 0; pass < 4; pass++)
        {
            var next = ApplyOnce(current);
            if (next is null) break;

            current = next;
            applied++;
        }

        return applied == 0
            ? new SelfCorrectionResult(text, 0)
            : new SelfCorrectionResult(current, applied);
    }

    /// <returns>The rewritten text, or null when no retraction survives the guards.</returns>
    private static string? ApplyOnce(string text)
    {
        var tokens = SpeechText.Tokenize(text);
        if (tokens.Count < 3) return null;

        for (var start = 0; start < tokens.Count; start++)
        {
            foreach (var marker in Markers)
            {
                if (!MatchesAt(tokens, start, marker)) continue;

                var rewritten = Rewrite(tokens, start, marker);
                if (rewritten is not null) return rewritten;
            }
        }

        return null;
    }

    private static bool MatchesAt(List<SpokenToken> tokens, int start, Marker marker)
    {
        if (start + marker.Words.Length > tokens.Count) return false;

        for (var i = 0; i < marker.Words.Length; i++)
        {
            var token = tokens[start + i];
            if (!string.Equals(token.Word, marker.Words[i], StringComparison.Ordinal)) return false;

            // Guard 1: the marker must not straddle punctuation. "I can't wait, no really"
            // carries the same words as a retraction and is not one. A comma is tolerated only
            // where the phrase itself normally has one ("sorry, I meant").
            if (i < marker.Words.Length - 1 &&
                token.EndsClause &&
                !Marker.CommaTolerant.Contains(token.Word))
            {
                return false;
            }
        }

        return true;
    }

    private static string? Rewrite(List<SpokenToken> tokens, int start, Marker marker)
    {
        var afterMarker = start + marker.Words.Length;
        if (afterMarker >= tokens.Count) return null;

        // Guard 5: per-marker traps. "actually no one told me" is not a retraction.
        if (marker.Traps.Contains(tokens[afterMarker].Word, StringComparer.Ordinal)) return null;

        // Guard 4: a weak marker only counts when the speaker paused in front of it.
        if (marker.Strength == MarkerStrength.Weak)
        {
            if (start == 0) return null;
            if (!tokens[start - 1].EndsClause) return null;
        }

        var replacementEnd = ClauseEnd(tokens, afterMarker);
        var replacementLength = replacementEnd - afterMarker;

        // Guard 3: there has to be something to replace it with, and it has to look like
        // content rather than the continuation of a sentence.
        if (replacementLength <= 0 || replacementLength > MaxReplacementWords) return null;

        // A replacement opening with a preposition or a conjunction is normally prose — "I
        // meant to call you". The exception is a parallel restatement, where the retracted span
        // opens with the very same word: "at three on Tuesday, wait no, at four on Friday".
        // That parallelism is itself the evidence that a phrase is being restated rather than a
        // sentence continuing, so it is the one thing allowed to lift the guard.
        if (SpeechText.BlockedFragmentStart.Contains(tokens[afterMarker].Word))
        {
            if (marker.RetractsSentence) return null;

            var mirrored = start - replacementLength;
            if (mirrored < 0) return null;
            if (!string.Equals(tokens[mirrored].Word, tokens[afterMarker].Word, StringComparison.Ordinal))
            {
                return null;
            }
        }

        int retractStart;

        if (marker.RetractsSentence)
        {
            // "scratch that" only reads as a retraction when it stands apart: it either opens a
            // sentence of its own or is set off by a comma. "I had to scratch that itch" does
            // neither.
            var opensSentence = start == 0 || tokens[start - 1].EndsSentence;
            var setOff = tokens[afterMarker - 1].EndsWithComma;
            if (!opensSentence && !setOff) return null;

            retractStart = ScratchStart(tokens, start);
            if (retractStart < 0 || retractStart >= start) return null;

            // Guard 2 is deliberately skipped here. A sentence-level retraction is meant to
            // reach back over the commas inside the sentence it is throwing away; that is the
            // whole difference between "scratch that" and "wait no".
        }
        else
        {
            retractStart = start - replacementLength;
            if (retractStart < 0) return null;

            // Guard 2: the retracted span may not reach back across a pause into a previous
            // thought. Whatever the speaker is correcting, they said it in the breath before
            // the marker.
            for (var i = retractStart; i < start - 1; i++)
            {
                if (tokens[i].EndsClause) return null;
            }
        }

        var head = SpeechText.Join(tokens, 0, retractStart);
        var tail = SpeechText.Join(tokens, afterMarker, tokens.Count);

        if (tail.Length == 0) return null;

        // The retracted words took their punctuation with them, so a head that now ends bare
        // mid-sentence is rejoined with a space and nothing else.
        var joined = head.Length == 0 ? tail : head + " " + tail;
        var tidied = Tidy(joined);

        return string.Equals(tidied, SpeechText.Join(tokens, 0, tokens.Count), StringComparison.Ordinal)
            ? null
            : tidied;
    }

    /// <summary>Index one past the last token of the clause starting at <paramref name="from"/>.</summary>
    private static int ClauseEnd(List<SpokenToken> tokens, int from)
    {
        for (var i = from; i < tokens.Count; i++)
        {
            if (tokens[i].EndsClause) return i + 1;
        }

        return tokens.Count;
    }

    /// <summary>
    /// Where a "scratch that" starts deleting from.
    /// </summary>
    /// <remarks>
    /// When the phrase opens a sentence of its own — "…Tuesday. Scratch that, Friday." — the
    /// thing being scratched is the sentence <i>before</i> it, not the one it is standing at
    /// the front of. When it is set off mid-sentence, the current sentence is what goes.
    /// </remarks>
    private static int ScratchStart(List<SpokenToken> tokens, int start)
    {
        var here = SentenceStart(tokens, start);
        return here == start && start > 0 ? SentenceStart(tokens, start - 1) : here;
    }

    /// <summary>Index of the first token of the sentence containing <paramref name="at"/>.</summary>
    private static int SentenceStart(List<SpokenToken> tokens, int at)
    {
        for (var i = at - 1; i >= 0; i--)
        {
            if (tokens[i].EndsSentence) return i + 1;
        }

        return 0;
    }

    /// <summary>
    /// Cleans up after a deletion: a dangling break, a lost capital, a doubled space.
    /// </summary>
    private static string Tidy(string text)
    {
        var trimmed = text.Trim();
        while (trimmed.StartsWith(',') || trimmed.StartsWith(';') || trimmed.StartsWith(':'))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        // "Let's meet Tuesday, wait no, Friday" leaves "Let's meet Friday" — but "Tuesday.
        // Scratch that, Friday" leaves a fragment that has to start with a capital again.
        return SpeechText.Capitalize(trimmed);
    }
}
