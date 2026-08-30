using Murmur.Core;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>Disfluencies that should go.</summary>
public sealed class FillerFilterPositiveTests
{
    [Fact]
    public void A_bare_filler_is_dropped()
    {
        FillerFilter.Apply("So um I think we should ship it").ShouldBe("So I think we should ship it");
    }

    [Fact]
    public void Several_fillers_go_at_once()
    {
        var result = FillerFilter.Analyse("Um so uh I think er we should ship it");

        result.Removed.ShouldBe(3);
        result.Text.ShouldBe("So I think we should ship it");
    }

    [Fact]
    public void The_punctuation_a_filler_carried_is_handed_back()
    {
        // "Well, um, fine" must not become "Well fine" — the pause was real, only the noise
        // filling it was not.
        FillerFilter.Apply("Well, um, fine.").ShouldBe("Well, fine.");
    }

    [Fact]
    public void A_leading_filler_does_not_leave_the_sentence_starting_with_a_comma()
    {
        FillerFilter.Apply("Um, hello there.").ShouldBe("Hello there.");
    }

    [Fact]
    public void Stretched_fillers_are_recognised()
    {
        FillerFilter.Apply("Ummm I think uhhh maybe").ShouldBe("I think maybe");
    }

    [Fact]
    public void A_filler_that_ends_the_sentence_keeps_the_full_stop_on_the_sentence()
    {
        FillerFilter.Apply("That's it um.").ShouldBe("That's it.");
    }

    [Fact]
    public void An_utterance_of_pure_hesitation_becomes_nothing()
    {
        // Deliberate: the engine refuses to inject whitespace, so a recording of nothing but
        // hesitation types nothing rather than typing "um".
        var result = FillerFilter.Analyse("Um, uh, er.");

        result.Removed.ShouldBe(3);
        result.Text.ShouldBeEmpty();
    }

    [Fact]
    public void Case_does_not_matter()
    {
        FillerFilter.Apply("UM I think UH so").ShouldBe("I think so");
    }
}

/// <summary>
/// Words that merely look like disfluencies, and must survive.
/// </summary>
/// <remarks>
/// Every entry on the strip list is a real word or a name somewhere. This is the battery that
/// proves whole-token matching is actually doing its job — a substring match would fail most of
/// these.
/// </remarks>
public sealed class FillerFilterNegativeTests
{
    [Theory]
    // The strip list as substrings of ordinary words.
    [InlineData("Ahmed sent the summary")]
    [InlineData("The German erratum was hers")]
    [InlineData("Her umbrella is under there")]
    [InlineData("He erred on the side of caution")]
    [InlineData("Number the columns")]
    [InlineData("The hummus was ahead of its time")]
    [InlineData("Mumbai in the summer")]
    // Joined forms that reduce to a filler if you only look at the letters.
    [InlineData("ah-ha, that explains it")]
    [InlineData("uh-huh, go on")]
    [InlineData("mm-hmm")]
    // Protected words.
    [InlineData("huh, that's odd")]
    [InlineData("Oh, I see")]
    [InlineData("Aha, found it")]
    // Nothing to do.
    [InlineData("A perfectly ordinary sentence")]
    [InlineData("")]
    [InlineData("   ")]
    public void Ordinary_text_is_left_exactly_as_it_was(string text)
    {
        var result = FillerFilter.Analyse(text);

        result.Removed.ShouldBe(0, $"\"{text}\" became \"{result.Text}\"");
        result.Text.ShouldBe(text);
    }

    [Fact]
    public void A_word_the_filler_list_would_swallow_is_not_a_filler_when_it_is_part_of_a_name()
    {
        // "Er" is the riskiest entry on the list by a distance — it is inside "her", "there",
        // "German", "error", "were". Only the stand-alone token is ever touched.
        FillerFilter.Apply("Werner told Hermann there were errors")
            .ShouldBe("Werner told Hermann there were errors");
    }

    [Fact]
    public void A_hyphenated_token_is_never_split_apart_to_find_a_filler()
    {
        FillerFilter.Analyse("The uh-oh moment").Removed.ShouldBe(0);
    }

    [Fact]
    public void Hedge_words_are_deliberately_not_removed()
    {
        // "Like", "actually" and "you know" carry meaning often enough that stripping them
        // changes what the user said. They are not on the list and must not creep onto it.
        const string text = "It was, like, actually fine, you know, basically";
        FillerFilter.Apply(text).ShouldBe(text);
    }

    [Fact]
    public void Recasing_only_happens_when_the_first_word_was_the_thing_removed()
    {
        // Removing a filler from the middle must not also silently recapitalise the sentence.
        FillerFilter.Apply("hello um there").ShouldBe("hello there");
    }
}
