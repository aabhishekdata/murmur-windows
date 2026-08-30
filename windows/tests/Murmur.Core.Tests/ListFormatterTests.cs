using Murmur.Core;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>
/// The spoken-enumeration pass: what must become a list.
/// </summary>
/// <remarks>
/// The reported bug was that a dictated list arrived as one run-on sentence. These are the
/// shapes a person actually speaks when they mean a list.
/// </remarks>
public sealed class ListFormatterPositiveTests
{
    [Fact]
    public void Spoken_numbers_become_a_numbered_list()
    {
        // The exact behaviour Wispr Flow documents: "just speak the numbers".
        ListFormatter.Format("one apples two bananas three oranges")
            .ShouldBe("1. Apples\n2. Bananas\n3. Oranges");
    }

    [Fact]
    public void Digits_the_recogniser_emitted_become_a_numbered_list()
    {
        ListFormatter.Format("1. Apples 2. Bananas 3. Oranges")
            .ShouldBe("1. Apples\n2. Bananas\n3. Oranges");
    }

    [Fact]
    public void Punctuated_numbers_become_a_numbered_list()
    {
        ListFormatter.Format("One, apples. Two, bananas. Three, oranges.")
            .ShouldBe("1. Apples\n2. Bananas\n3. Oranges");
    }

    [Fact]
    public void Ordinals_become_a_numbered_list()
    {
        ListFormatter.Format("First, call the bank. Second, email Dave. Third, book the flight.")
            .ShouldBe("1. Call the bank\n2. Email Dave\n3. Book the flight");
    }

    [Fact]
    public void Number_one_number_two_becomes_a_numbered_list()
    {
        ListFormatter.Format("Number one, milk. Number two, eggs.")
            .ShouldBe("1. Milk\n2. Eggs");
    }

    [Fact]
    public void Two_markers_are_enough()
    {
        var result = ListFormatter.Analyse("One, milk. Two, eggs.");

        result.Style.ShouldBe(ListStyle.Numbered);
        result.ItemCount.ShouldBe(2);
        result.Text.ShouldBe("1. Milk\n2. Eggs");
    }

    [Fact]
    public void A_lead_in_sentence_is_kept_on_its_own_line()
    {
        // "Here's my shopping list" is not item zero. It stays as spoken, above the list.
        ListFormatter.Format("Here's my shopping list. One, milk. Two, eggs. Three, bread.")
            .ShouldBe("Here's my shopping list.\n1. Milk\n2. Eggs\n3. Bread");
    }

    [Fact]
    public void Prose_after_the_last_item_is_not_swallowed_by_it()
    {
        ListFormatter.Format("One, milk. Two, eggs. Then I went home.")
            .ShouldBe("1. Milk\n2. Eggs\nThen I went home.");
    }

    [Fact]
    public void A_sentence_length_item_keeps_its_full_stop()
    {
        // Short items lose the recogniser's stop because "1. Apples." reads as a typo. A
        // sentence-length item is prose and keeps its punctuation.
        ListFormatter.Format("First, I was scared. Second, I didn't want to hurt her.")
            .ShouldBe("1. I was scared\n2. I didn't want to hurt her.");
    }

    [Fact]
    public void Ordinal_digits_are_recognised()
    {
        ListFormatter.Format("1st, milk. 2nd, eggs. 3rd, bread.")
            .ShouldBe("1. Milk\n2. Eggs\n3. Bread");
    }

    [Fact]
    public void The_run_stops_where_the_counting_stops()
    {
        // Four items, then unrelated prose. Only the counting becomes a list.
        var result = ListFormatter.Analyse("One, milk. Two, eggs. Three, bread. Four, butter.");

        result.ItemCount.ShouldBe(4);
        result.Text.ShouldBe("1. Milk\n2. Eggs\n3. Bread\n4. Butter");
    }

    [Fact]
    public void A_brand_name_keeps_its_lowercase_first_letter()
    {
        // Capitalising the first letter of an item must not turn "iPhone" into "IPhone".
        ListFormatter.Format("One, iPhone. Two, iPad.")
            .ShouldBe("1. iPhone\n2. iPad");
    }
}

/// <summary>
/// The bare comma-separated pass: "milk, eggs, bread, butter".
/// </summary>
/// <remarks>
/// Deliberately much narrower than the numbered pass. It only fires when the whole utterance
/// is short noun-ish fragments, because a comma is otherwise the most common punctuation in
/// ordinary speech and bulleting a sentence would be a disaster.
/// </remarks>
public sealed class ListFormatterCommaListTests
{
    [Fact]
    public void A_bare_shopping_list_becomes_bullets()
    {
        var result = ListFormatter.Analyse("milk, eggs, bread, butter");

        result.Style.ShouldBe(ListStyle.Bulleted);
        result.Text.ShouldBe("- Milk\n- Eggs\n- Bread\n- Butter");
    }

    [Fact]
    public void A_trailing_and_is_part_of_the_list_not_the_item()
    {
        ListFormatter.Format("milk, eggs, bread, and butter.")
            .ShouldBe("- Milk\n- Eggs\n- Bread\n- Butter");
    }

    [Fact]
    public void Multi_word_items_are_allowed()
    {
        ListFormatter.Format("olive oil, whole milk, brown bread")
            .ShouldBe("- Olive oil\n- Whole milk\n- Brown bread");
    }

    [Fact]
    public void Two_items_are_not_a_list()
    {
        // Two comma-separated fragments is how people speak, not how they enumerate.
        ListFormatter.Analyse("milk, eggs").WasFormatted.ShouldBeFalse();
    }

    // ---- The widening: a colon lead-in, and four-word fragments ----

    [Fact]
    public void A_colon_announces_a_list_and_the_lead_in_is_kept()
    {
        // A colon is the one piece of sentence structure that argues *for* a list. Everything
        // in front of it is kept verbatim, exactly as the numbered pass keeps a preamble.
        ListFormatter.Format("Here's what I need: milk, eggs, bread")
            .ShouldBe("Here's what I need:\n- Milk\n- Eggs\n- Bread");
    }

    [Fact]
    public void A_colon_list_still_has_to_pass_every_fragment_test()
    {
        // The colon is a licence to look, not a licence to reformat. A clause after it is
        // still a clause.
        ListFormatter.Analyse("Here's the thing: it was cold, it was dark, it was wet")
            .WasFormatted.ShouldBeFalse();
    }

    [Fact]
    public void A_long_lead_in_is_prose_that_happens_to_contain_a_colon()
    {
        var text = "After a great deal of thought and rather more coffee than was strictly "
                 + "sensible I concluded the following: milk, eggs, bread";

        ListFormatter.Analyse(text).WasFormatted.ShouldBeFalse();
    }

    [Fact]
    public void A_four_word_fragment_is_now_allowed()
    {
        // Four words still cannot hold a clause — verbs and pronouns are refused outright — but
        // it does hold the compound nouns people actually dictate.
        ListFormatter.Format("extra virgin olive oil, large red onions, plain white bread")
            .ShouldBe("- Extra virgin olive oil\n- Large red onions\n- Plain white bread");
    }

    [Fact]
    public void A_five_word_fragment_is_still_too_long()
    {
        ListFormatter.Analyse("one two three four five six, a, b").WasFormatted.ShouldBeFalse();
    }

    [Fact]
    public void A_second_colon_is_prose()
    {
        ListFormatter.Analyse("Note: this is the thing: milk, eggs, bread")
            .WasFormatted.ShouldBeFalse();
    }
}

/// <summary>
/// The far more important half: prose that must survive untouched.
/// </summary>
/// <remarks>
/// Mangling a sentence that merely contains "first" or "one" is worse than never recognising a
/// list at all, because the user cannot see it happen — the text has already been typed into
/// someone else's application by the time they read it.
/// </remarks>
public sealed class ListFormatterNegativeTests
{
    [Theory]
    // A number behind a determiner is a modifier, not a marker.
    [InlineData("the first time I met her was in Paris")]
    [InlineData("The first thing I noticed was the second door on the left.")]
    [InlineData("On the one hand it is cheap, on the other hand it is slow")]
    // A number behind a verb or preposition is a quantity.
    [InlineData("I only have one question")]
    [InlineData("He was one of two children.")]
    [InlineData("I need one apple two oranges")]
    [InlineData("Pick one or two and let me know")]
    // Counting has to actually count.
    [InlineData("two coffees please")]
    [InlineData("There were two options; one was cheaper.")]
    [InlineData("one two three")]
    // "First of all" is a discourse marker, not item one.
    [InlineData("First of all, thanks for coming. Second of all, sorry I'm late.")]
    // Ordinary sentences with commas.
    [InlineData("It was cold, dark, and wet.")]
    [InlineData("I went to the store, bought some milk, and came home.")]
    [InlineData("Yeah, sure, okay.")]
    [InlineData("Hi John, how are you doing today?")]
    // Nothing to do.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Just a normal sentence with no numbers in it at all.")]
    public void Prose_is_left_exactly_as_it_was(string text)
    {
        var result = ListFormatter.Analyse(text);

        result.WasFormatted.ShouldBeFalse($"\"{text}\" is not a list, but became: {result.Text}");
        result.Text.ShouldBe(text);
        result.Style.ShouldBe(ListStyle.None);
        result.ItemCount.ShouldBe(0);
    }

    [Fact]
    public void An_item_longer_than_the_cap_calls_the_whole_thing_off()
    {
        // A long stretch between two number words is prose that happens to contain numbers.
        var text = "One, I went to the shop and it was closed and then it started raining hard. "
                 + "Two, eggs.";

        ListFormatter.Analyse(text).WasFormatted.ShouldBeFalse();
    }

    [Fact]
    public void Text_that_is_already_structured_is_never_restructured()
    {
        const string text = "1. Apples\n2. Bananas";
        ListFormatter.Format(text).ShouldBe(text);
    }

    [Fact]
    public void A_skipped_number_breaks_the_run()
    {
        // One then three is not counting, and half a list is worse than none.
        ListFormatter.Analyse("One, milk. Three, bread.").WasFormatted.ShouldBeFalse();
    }

    [Fact]
    public void Markers_in_descending_order_are_not_a_list()
    {
        ListFormatter.Analyse("Two, bananas. One, apples.").WasFormatted.ShouldBeFalse();
    }

    [Fact]
    public void Cardinals_and_ordinals_are_never_mixed_into_one_run()
    {
        // "One … second …" is not an enumeration anybody speaks; treating it as one would mean
        // the marker table is matching too loosely.
        ListFormatter.Analyse("One, milk. Second, eggs.").WasFormatted.ShouldBeFalse();
    }

    [Fact]
    public void Numbers_above_the_ceiling_are_quantities()
    {
        ListFormatter.Analyse("Fifty, sixty, seventy").WasFormatted.ShouldBeFalse();
    }
}
