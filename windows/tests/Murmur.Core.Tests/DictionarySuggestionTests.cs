using Murmur.Core;
using Murmur.Dictionary;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>
/// Mining transcript history for corrections the user has not made yet.
/// </summary>
/// <remarks>
/// Every suggestion is offered rather than applied, so the bar for producing one is lower than
/// for a pass that rewrites text — but a picker full of nonsense is worse than an empty one, so
/// the band still has to hold.
/// </remarks>
public sealed class DictionarySuggestionTests
{
    private static TranscriptRecord Said(string text) =>
        new() { At = DateTimeOffset.Now, Text = text };

    [Fact]
    public void A_near_miss_of_a_known_term_is_suggested()
    {
        // The user told Murmur "Claude Code" exists. A transcript saying "cloud code" is that
        // term misheard, and this is the signal that needs no new permission to read.
        var suggestions = DictionarySuggestions.For(
            [Said("I use cloud code every day")],
            [DictionaryEntry.Term("Claude Code")]);

        var suggestion = suggestions.ShouldHaveSingleItem();
        suggestion.Heard.ShouldBe("cloud code");
        suggestion.Intended.ShouldBe("Claude Code");
    }

    [Fact]
    public void Repeats_are_counted_rather_than_listed_twice()
    {
        var suggestions = DictionarySuggestions.For(
            [Said("cloud code again"), Said("more cloud code"), Said("cloud code")],
            [DictionaryEntry.Term("Claude Code")]);

        suggestions.ShouldHaveSingleItem().Occurrences.ShouldBe(3);
    }

    [Fact]
    public void The_term_spelled_correctly_is_never_suggested()
    {
        DictionarySuggestions.For(
            [Said("I use Claude Code every day")],
            [DictionaryEntry.Term("Claude Code")]).ShouldBeEmpty();
    }

    [Fact]
    public void Something_already_corrected_is_not_suggested_again()
    {
        DictionarySuggestions.For(
            [Said("I use cloud code every day")],
            [
                DictionaryEntry.Term("Claude Code"),
                DictionaryEntry.Correction("cloud code", "Claude Code"),
            ]).ShouldBeEmpty();
    }

    [Fact]
    public void A_dismissed_suggestion_stays_dismissed()
    {
        var first = DictionarySuggestions.For(
            [Said("I use cloud code every day")],
            [DictionaryEntry.Term("Claude Code")]).ShouldHaveSingleItem();

        DictionarySuggestions.For(
            [Said("I use cloud code every day")],
            [DictionaryEntry.Term("Claude Code")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first.Key }).ShouldBeEmpty();
    }

    [Fact]
    public void An_unrelated_word_is_too_far_away_to_suggest()
    {
        DictionarySuggestions.For(
            [Said("The weather is lovely today")],
            [DictionaryEntry.Term("Anthropic")]).ShouldBeEmpty();
    }

    [Fact]
    public void Short_terms_are_skipped_entirely()
    {
        // Below the length floor, one edit is most of the word and everything looks like a
        // near miss.
        DictionarySuggestions.For(
            [Said("I saw a cat and a bat and a hat")],
            [DictionaryEntry.Term("rat")]).ShouldBeEmpty();
    }

    [Fact]
    public void A_disabled_term_produces_no_suggestions()
    {
        DictionarySuggestions.For(
            [Said("I use cloud code")],
            [DictionaryEntry.Term("Claude Code") with { IsEnabled = false }]).ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_history_or_dictionary_produces_nothing()
    {
        DictionarySuggestions.For([], [DictionaryEntry.Term("Anthropic")]).ShouldBeEmpty();
        DictionarySuggestions.For([Said("anything at all")], []).ShouldBeEmpty();
    }

    [Fact]
    public void The_offer_is_capped_so_the_hub_is_never_flooded()
    {
        var records = Enumerable.Range(0, 30).Select(i => Said($"anthropi{(char)('a' + i)} again")).ToList();
        var suggestions = DictionarySuggestions.For(records, [DictionaryEntry.Term("Anthropic")]);

        suggestions.Count.ShouldBeLessThanOrEqualTo(DictionarySuggestions.MaxSuggestions);
    }

    [Fact]
    public void The_most_frequent_mishearing_is_offered_first()
    {
        var records = new List<TranscriptRecord>
        {
            Said("anthropik once"),
            Said("anthropicc twice"),
            Said("anthropicc again"),
            Said("anthropicc a third time"),
        };

        var suggestions = DictionarySuggestions.For(records, [DictionaryEntry.Term("Anthropic")]);

        suggestions[0].Heard.ShouldBe("anthropicc");
        suggestions[0].Occurrences.ShouldBe(3);
    }

    [Fact]
    public void A_suggestion_becomes_an_ordinary_correction_entry()
    {
        // No hidden second dictionary: accepting a suggestion writes exactly what the user
        // would have typed by hand.
        var suggestion = new DictionarySuggestion("cloud code", "Claude Code", 2, 0.8);
        var entry = suggestion.ToEntry();

        entry.Kind.ShouldBe(EntryKind.Correction);
        entry.Hear.ShouldBe("cloud code");
        entry.Write.ShouldBe("Claude Code");
        entry.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void The_dismissal_key_ignores_casing()
    {
        new DictionarySuggestion("Cloud Code", "Claude Code", 1, 0.8).Key
            .ShouldBe(new DictionarySuggestion("cloud code", "claude code", 1, 0.8).Key);
    }
}

/// <summary>The similarity measure the band is built on.</summary>
public sealed class SimilarityTests
{
    [Fact]
    public void Identical_strings_score_one()
    {
        DictionarySuggestions.Similarity("anthropic", "anthropic").ShouldBe(1);
    }

    [Fact]
    public void One_edit_in_ten_characters_scores_nine_tenths()
    {
        DictionarySuggestions.Similarity("anthropic", "anthropik").ShouldBe(8.0 / 9, 0.001);
    }

    [Fact]
    public void Wildly_different_lengths_score_zero_without_measuring()
    {
        DictionarySuggestions.Similarity("hi", "anthropic corporation").ShouldBe(0);
    }

    [Fact]
    public void Nothing_in_common_scores_low()
    {
        DictionarySuggestions.Similarity("weather", "vercel").ShouldBeLessThan(0.5);
    }
}

/// <summary>Counting what a new rule would have fixed.</summary>
public sealed class BackfillTests
{
    private static TranscriptRecord Said(string text) =>
        new() { At = DateTimeOffset.Now, Text = text };

    [Fact]
    public void Past_transcripts_containing_the_trigger_are_counted()
    {
        var records = new List<TranscriptRecord>
        {
            Said("I use cloud code daily"),
            Said("CloudCode is great"),
            Said("nothing relevant here"),
        };

        DictionarySuggestions.Backfill(records, "cloud code").ShouldBe(2);
    }

    [Fact]
    public void A_trigger_that_matches_nothing_counts_zero()
    {
        // The signal that a hand-typed rule has a typo in it, shown before the next dictation
        // rather than after it.
        DictionarySuggestions.Backfill([Said("nothing relevant")], "cloud code").ShouldBe(0);
    }

    [Fact]
    public void An_empty_trigger_counts_zero()
    {
        DictionarySuggestions.Backfill([Said("anything")], "   ").ShouldBe(0);
    }

    [Fact]
    public void Word_fences_apply_to_the_count_too()
    {
        DictionarySuggestions.Backfill([Said("Cloudflare is fine")], "cloud").ShouldBe(0);
    }
}
