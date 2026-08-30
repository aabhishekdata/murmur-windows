using Murmur.Core;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>Expanding a spoken trigger into stored text.</summary>
public sealed class SnippetExpanderTests
{
    private static SnippetEntry Snippet(string trigger, string text, bool enabled = true) =>
        new() { Trigger = trigger, Text = text, IsEnabled = enabled };

    [Fact]
    public void A_trigger_expands_to_its_text()
    {
        var expander = new SnippetExpander([Snippet("my address", "12 Bridge Street")]);

        expander.Expand("Send it to my address please.").Text
            .ShouldBe("Send it to 12 Bridge Street please.");
    }

    [Fact]
    public void A_multi_line_body_survives_expansion()
    {
        var expander = new SnippetExpander([Snippet("my signature", "Best,\nPat")]);

        expander.Expand("my signature").Text.ShouldBe("Best,\nPat");
    }

    [Fact]
    public void The_longest_trigger_wins()
    {
        // Same rule the dictionary follows: a trigger that contains another must not be
        // pre-empted by the shorter one.
        var expander = new SnippetExpander(
        [
            Snippet("my address", "SHORT"),
            Snippet("my address in full", "LONG"),
        ]);

        expander.Expand("Use my address in full here").Text.ShouldBe("Use LONG here");
    }

    [Fact]
    public void A_trigger_never_bites_into_a_longer_word()
    {
        var expander = new SnippetExpander([Snippet("sig", "SIGNATURE")]);

        // "sig" is below the minimum trigger length, so it is not even compiled — but the
        // fencing matters for longer triggers too, checked below.
        expander.IsEmpty.ShouldBeFalse();
        expander.Expand("The design is significant").Text.ShouldBe("The design is significant");
    }

    [Fact]
    public void Word_fences_hold_for_a_real_trigger()
    {
        var expander = new SnippetExpander([Snippet("addr", "12 Bridge Street")]);

        expander.Expand("The address bar is fine").Text.ShouldBe("The address bar is fine");
        expander.Expand("Type addr here").Text.ShouldBe("Type 12 Bridge Street here");
    }

    [Fact]
    public void Glued_and_hyphenated_forms_still_match()
    {
        var expander = new SnippetExpander([Snippet("stand up", "Yesterday: shipped the bar.")]);

        expander.Expand("standup").Text.ShouldBe("Yesterday: shipped the bar.");
        expander.Expand("stand-up").Text.ShouldBe("Yesterday: shipped the bar.");
    }

    [Fact]
    public void A_body_containing_regex_substitutions_is_typed_literally()
    {
        // A MatchEvaluator rather than a replacement string. "$1" in a user's own snippet is
        // text, not a capture group.
        var expander = new SnippetExpander([Snippet("price note", "Costs $1 and $& more")]);

        expander.Expand("price note").Text.ShouldBe("Costs $1 and $& more");
    }

    [Fact]
    public void A_disabled_snippet_does_not_expand()
    {
        var expander = new SnippetExpander([Snippet("my address", "12 Bridge Street", enabled: false)]);

        expander.IsEmpty.ShouldBeTrue();
        expander.Expand("my address").Text.ShouldBe("my address");
    }

    [Fact]
    public void A_trigger_below_the_minimum_length_is_never_compiled()
    {
        // A one- or two-letter trigger fires constantly in ordinary speech, and a false snippet
        // match drops a paragraph into the middle of a sentence.
        new SnippetExpander([Snippet("a", "NOPE")]).IsEmpty.ShouldBeTrue();
        new SnippetExpander([Snippet("hi", "NOPE")]).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void An_empty_body_is_not_a_snippet()
    {
        new SnippetExpander([Snippet("my address", "")]).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Expansion_reports_what_fired_and_how_often()
    {
        var expander = new SnippetExpander([Snippet("my address", "HERE")]);
        var result = expander.Expand("my address and my address");

        result.WasChanged.ShouldBeTrue();
        result.Applied.ShouldHaveSingleItem().Count.ShouldBe(2);
    }

    [Fact]
    public void Text_with_no_trigger_in_it_is_untouched()
    {
        var expander = new SnippetExpander([Snippet("my address", "HERE")]);
        const string text = "An ordinary sentence about somewhere else entirely.";

        expander.Expand(text).Text.ShouldBe(text);
    }
}

/// <summary>Storing snippets on disk.</summary>
public sealed class SnippetStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "murmur-tests", $"{Guid.NewGuid():N}.jsonl");

    [Fact]
    public void Snippets_round_trip_through_the_file()
    {
        var path = TempPath();

        try
        {
            var store = new SnippetStore(path);
            store.Add(new SnippetEntry { Trigger = "my address", Text = "12 Bridge Street\nManchester" });

            var reopened = new SnippetStore(path);
            var entry = reopened.Entries.ShouldHaveSingleItem();

            entry.Trigger.ShouldBe("my address");
            entry.Text.ShouldBe("12 Bridge Street\nManchester", "a multi-line body must survive");
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void A_body_past_the_ceiling_is_cut_rather_than_stored_whole()
    {
        var path = TempPath();

        try
        {
            var store = new SnippetStore(path);
            store.Add(new SnippetEntry
            {
                Trigger = "long one",
                Text = new string('x', SnippetEntry.MaxLength + 500),
            });

            store.Entries.ShouldHaveSingleItem().Text.Length.ShouldBe(SnippetEntry.MaxLength);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void One_corrupt_line_costs_one_snippet_rather_than_the_file()
    {
        var path = TempPath();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path,
            [
                "{\"Trigger\":\"first\",\"Text\":\"one\",\"IsEnabled\":true}",
                "{ this is not json",
                "{\"Trigger\":\"second\",\"Text\":\"two\",\"IsEnabled\":true}",
            ]);

            new SnippetStore(path).Entries.Count.ShouldBe(2);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void Search_covers_both_the_trigger_and_the_body()
    {
        var path = TempPath();

        try
        {
            var store = new SnippetStore(path);
            store.Add(new SnippetEntry { Trigger = "my address", Text = "12 Bridge Street" });
            store.Add(new SnippetEntry { Trigger = "sign off", Text = "Kind regards" });

            store.Search("bridge").ShouldHaveSingleItem().Trigger.ShouldBe("my address");
            store.Search("sign").ShouldHaveSingleItem().Text.ShouldBe("Kind regards");
            store.Search("").Count.ShouldBe(2);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void A_preview_flattens_line_breaks_and_elides()
    {
        var entry = new SnippetEntry { Text = "First line\nSecond line" };

        entry.Preview().ShouldBe("First line Second line");
        entry.Preview(8).Length.ShouldBeLessThanOrEqualTo(8);
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* best effort */ }
    }
}

/// <summary>The warnings shown while a snippet is being written.</summary>
public sealed class SnippetWarningTests
{
    [Fact]
    public void A_very_common_word_is_flagged_as_a_trigger()
    {
        var warnings = SnippetWarning.Check(new SnippetEntry { Trigger = "the", Text = "x" });

        warnings.ShouldNotBeEmpty();
    }

    [Fact]
    public void A_two_letter_trigger_is_flagged_as_ignored()
    {
        var warnings = SnippetWarning.Check(new SnippetEntry { Trigger = "hi", Text = "x" });

        warnings.ShouldContain(w => w.Message.Contains("shorter than", StringComparison.Ordinal));
    }

    [Fact]
    public void A_sensible_snippet_produces_no_warnings()
    {
        SnippetWarning.Check(new SnippetEntry
        {
            Trigger = "my address",
            Text = "12 Bridge Street",
        }).ShouldBeEmpty();
    }
}
