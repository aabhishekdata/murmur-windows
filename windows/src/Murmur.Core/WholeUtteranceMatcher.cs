using System.Globalization;
using System.Text;

namespace Murmur.Core;

/// <summary>A saved prompt that the whole utterance asked for.</summary>
/// <param name="Snippet">The prompt.</param>
/// <param name="Submit">
/// Whether a submit phrase followed the trigger: "review prompt press enter" pastes the prompt
/// and then sends it.
/// </param>
public readonly record struct WholeUtteranceMatch(SnippetEntry Snippet, bool Submit);

/// <summary>
/// Recognises an utterance that is nothing but a saved prompt's trigger.
/// </summary>
/// <remarks>
/// <para>
/// The inline <see cref="SnippetExpander"/> looks for a trigger anywhere in a sentence. A saved
/// prompt is the opposite: it fires only when the trigger is <i>all</i> that was said, so "review
/// prompt" pastes the prompt and "please review prompt" or "the review prompt is long" are left
/// alone as ordinary speech.
/// </para>
/// <para>
/// "All that was said" is decided after <see cref="Normalize"/> has taken away everything a
/// recogniser varies between takes and the user does not control: case, punctuation and symbols,
/// hyphens, and how many spaces there are. The comparison then ignores the spaces too, so a
/// recogniser that runs the words together ("reviewprompt") still matches — the same tolerance
/// the dictionary and the inline expander have for glued words. What is left must be identical;
/// there is no fuzzy matching, because pasting a paragraph nobody asked for is worse than
/// typing the two words that were said.
/// </para>
/// <para>
/// A trailing submit phrase is accepted here even without the full stop
/// <see cref="VoiceCommands"/> insists on. That pass guards against prose that merely ends in
/// "press enter"; an utterance that is exactly a trigger and a submit phrase is not prose.
/// </para>
/// </remarks>
public sealed class WholeUtteranceMatcher
{
    private readonly Dictionary<string, SnippetEntry> _byKey = new(StringComparer.Ordinal);

    /// <summary>Indexes the enabled whole-utterance prompts.</summary>
    /// <param name="entries">Every snippet. Inline, disabled and empty ones are ignored.</param>
    /// <remarks>Two prompts with the same trigger: the first in the file wins.</remarks>
    public WholeUtteranceMatcher(IEnumerable<SnippetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var entry in entries)
        {
            if (!entry.IsEnabled || entry.Match != SnippetMatch.WholeUtterance || entry.Text.Length == 0) continue;

            var key = Key(Normalize(entry.Trigger));
            if (key.Length < SnippetExpander.MinTriggerLength) continue;

            _byKey.TryAdd(key, entry);
        }
    }

    /// <summary>True when no prompt can match.</summary>
    public bool IsEmpty => _byKey.Count == 0;

    /// <summary>The prompt <paramref name="text"/> is, if it is one.</summary>
    /// <param name="text">The utterance, after the earlier passes.</param>
    /// <param name="allowSubmitPhrase">Whether a trailing "press enter" may follow the trigger.</param>
    /// <returns>The prompt and whether to submit it, or null.</returns>
    public WholeUtteranceMatch? Match(string text, bool allowSubmitPhrase)
    {
        if (_byKey.Count == 0 || string.IsNullOrWhiteSpace(text)) return null;

        var normalized = Normalize(text);
        if (normalized.Length == 0) return null;

        if (_byKey.TryGetValue(Key(normalized), out var entry)) return new WholeUtteranceMatch(entry, Submit: false);
        if (!allowSubmitPhrase) return null;

        var words = normalized.Split(' ');
        foreach (var phrase in VoiceCommands.SubmitPhrases)
        {
            var start = words.Length - phrase.Length;
            if (start < 1 || !words.AsSpan(start).SequenceEqual(phrase)) continue;

            if (_byKey.TryGetValue(string.Concat(words.AsSpan(0, start)), out entry))
            {
                return new WholeUtteranceMatch(entry, Submit: true);
            }
        }

        return null;
    }

    /// <summary>Reduces text to what a whole-utterance match compares.</summary>
    /// <param name="text">A trigger, or what the recogniser heard.</param>
    /// <returns>
    /// NFC, lower case (culture-invariant), dashes turned into spaces, every other punctuation
    /// mark and symbol removed, and runs of whitespace collapsed to one space, trimmed.
    /// </returns>
    /// <remarks>
    /// NFC first, for the reason the dictionary gives: a decomposed accent is a different
    /// sequence of code points from a composed one, and an accented trigger would never match.
    /// Lower-casing is invariant so Turkish <c>İ</c> does not fold differently on a Turkish PC.
    /// Enumerated by <see cref="Rune"/>, so an emoji is one symbol to drop rather than two
    /// surrogate halves to keep.
    /// </remarks>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var source = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var builder = new StringBuilder(source.Length);
        var pendingSpace = false;

        foreach (var rune in source.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);

            if (Rune.IsWhiteSpace(rune) || category == UnicodeCategory.DashPunctuation)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (IsPunctuationOrSymbol(category)) continue;

            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private static string Key(string normalized) => normalized.Replace(" ", string.Empty, StringComparison.Ordinal);

    private static bool IsPunctuationOrSymbol(UnicodeCategory category) => category is
        UnicodeCategory.ConnectorPunctuation or
        UnicodeCategory.OpenPunctuation or
        UnicodeCategory.ClosePunctuation or
        UnicodeCategory.InitialQuotePunctuation or
        UnicodeCategory.FinalQuotePunctuation or
        UnicodeCategory.OtherPunctuation or
        UnicodeCategory.MathSymbol or
        UnicodeCategory.CurrencySymbol or
        UnicodeCategory.ModifierSymbol or
        UnicodeCategory.OtherSymbol;
}
