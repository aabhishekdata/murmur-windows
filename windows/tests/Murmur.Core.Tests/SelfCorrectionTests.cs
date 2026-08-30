using Murmur.Core;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>Spoken retractions that should be honoured.</summary>
public sealed class SelfCorrectionPositiveTests
{
    [Fact]
    public void Wait_no_replaces_the_word_in_front_of_it()
    {
        SelfCorrection.Apply("Let's meet Tuesday, wait no, Friday.")
            .ShouldBe("Let's meet Friday.");
    }

    [Fact]
    public void The_span_removed_matches_the_length_of_the_replacement()
    {
        // Symmetric substitution: three words in, three words out. People restate the same
        // shape when they correct themselves.
        SelfCorrection.Apply("Let's meet at three on Tuesday, wait no, at four on Friday.")
            .ShouldBe("Let's meet at four on Friday.");
    }

    [Fact]
    public void Actually_no_is_a_retraction()
    {
        SelfCorrection.Apply("The deadline is Monday, actually no, Wednesday.")
            .ShouldBe("The deadline is Wednesday.");
    }

    [Fact]
    public void Sorry_i_meant_is_a_retraction()
    {
        SelfCorrection.Apply("Send it to Dave, sorry I meant Steve.")
            .ShouldBe("Send it to Steve.");
    }

    [Fact]
    public void I_mean_after_a_pause_is_a_retraction()
    {
        SelfCorrection.Apply("The file is in Documents, I mean Downloads.")
            .ShouldBe("The file is in Downloads.");
    }

    [Fact]
    public void Scratch_that_throws_away_the_sentence_before_it()
    {
        SelfCorrection.Apply("Let's meet Tuesday at three. Scratch that, Friday at noon.")
            .ShouldBe("Friday at noon.");
    }

    [Fact]
    public void Scratch_that_reaches_back_over_the_commas_inside_the_sentence_it_discards()
    {
        // The one place the "must not cross a pause" guard is deliberately relaxed: a
        // sentence-level retraction is meant to swallow the commas inside its sentence.
        SelfCorrection.Apply("Book the room, order the food, and call Dave. Scratch that, cancel it.")
            .ShouldBe("Cancel it.");
    }

    [Fact]
    public void Two_retractions_in_one_breath_both_fire()
    {
        var result = SelfCorrection.Analyse("Meet Tuesday, wait no, Thursday, wait no, Friday.");

        result.Applied.ShouldBe(2);
        result.Text.ShouldBe("Meet Friday.");
    }

    [Fact]
    public void A_retraction_that_removes_the_whole_lead_in_leaves_the_replacement()
    {
        SelfCorrection.Apply("Tuesday, wait no, Friday.").ShouldBe("Friday.");
    }
}

/// <summary>
/// Prose containing the same words, which must survive untouched.
/// </summary>
/// <remarks>
/// This pass <i>deletes words the user said</i>, and they cannot see it happen — the text is
/// already in somebody else's application by the time they read it. That makes this battery the
/// most important one in the suite.
/// </remarks>
public sealed class SelfCorrectionNegativeTests
{
    [Theory]
    // "wait no" split by a comma is not a retraction.
    [InlineData("I can't wait, no really, it's fine")]
    [InlineData("Please wait. No rush at all.")]
    // "actually" on its own is not a marker.
    [InlineData("I actually agree with you")]
    [InlineData("Actually that's a good point")]
    // The "no one" trap.
    [InlineData("Well actually no one told me about it")]
    [InlineData("But actually no more excuses")]
    // "I mean" inside a sentence.
    [InlineData("Do you know what I mean, Dave?")]
    [InlineData("I mean it.")]
    [InlineData("That is not what I mean.")]
    [InlineData("You know what I mean by that")]
    // "I meant" as ordinary past tense.
    [InlineData("I meant to call you yesterday")]
    [InlineData("That is not what I meant.")]
    // "scratch that" as a verb phrase.
    [InlineData("I had to scratch that itch eventually")]
    [InlineData("Scratch that one off the list")]
    // "no, seriously" is agreement, not retraction.
    [InlineData("No, seriously, that's fine")]
    // A marker with nothing after it retracts nothing.
    [InlineData("Everything is fine, I mean")]
    // Nothing to do.
    [InlineData("A perfectly ordinary sentence about Tuesday and Friday")]
    [InlineData("")]
    [InlineData("   ")]
    public void Prose_is_left_exactly_as_it_was(string text)
    {
        var result = SelfCorrection.Analyse(text);

        result.Applied.ShouldBe(0, $"\"{text}\" became \"{result.Text}\"");
        result.Text.ShouldBe(text);
    }

    [Fact]
    public void A_retraction_may_not_reach_back_across_a_pause()
    {
        // "Well," is one word and the replacement is two, so honouring this would have to eat
        // into the clause before the comma. It does not fire at all instead.
        SelfCorrection.Analyse("Well, I mean, it depends.").WasChanged.ShouldBeFalse();
    }

    [Fact]
    public void A_replacement_that_opens_with_a_function_word_is_ordinary_prose()
    {
        SelfCorrection.Analyse("The plan, I mean, of course, was always Friday")
            .WasChanged.ShouldBeFalse();
    }

    [Fact]
    public void A_replacement_longer_than_the_cap_is_a_new_thought_not_a_restatement()
    {
        // Past a handful of words the speaker is saying something new, and symmetric
        // substitution would delete an unrelated span of the same length.
        var text = "Tuesday, wait no, I have completely changed my mind about the whole thing";

        SelfCorrection.Analyse(text).WasChanged.ShouldBeFalse();
    }

    [Fact]
    public void No_wait_is_deliberately_not_a_marker()
    {
        // Considered and dropped: "I said no, wait for me" is the same token sequence as a
        // retraction, with a comma in the same place, and no positional guard separates them.
        SelfCorrection.Analyse("I said no, wait for me").WasChanged.ShouldBeFalse();
        SelfCorrection.Analyse("Tuesday, no wait, Friday.").WasChanged.ShouldBeFalse();
    }

    [Fact]
    public void The_pass_always_terminates()
    {
        // The rewrite loop is bounded. A rule that somehow stopped shortening the text must not
        // be able to spin.
        var text = string.Join(", wait no, ", Enumerable.Repeat("Tuesday", 40));

        var result = SelfCorrection.Analyse(text);
        result.Applied.ShouldBeLessThanOrEqualTo(4);
    }
}
