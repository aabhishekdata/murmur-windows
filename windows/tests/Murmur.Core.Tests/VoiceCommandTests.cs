using Murmur.Core;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>Trailing commands that should submit.</summary>
public sealed class VoiceCommandPositiveTests
{
    [Fact]
    public void A_trailing_press_enter_submits_and_is_not_typed()
    {
        var result = VoiceCommands.Extract("Tell Dave I'll be ten minutes late. Press enter.");

        result.Submit.ShouldBeTrue();
        result.Text.ShouldBe("Tell Dave I'll be ten minutes late.");
    }

    [Theory]
    [InlineData("Send it. Press enter.")]
    [InlineData("Send it. Press return.")]
    [InlineData("Send it. Hit enter.")]
    [InlineData("Send it. Hit return.")]
    public void Every_phrasing_is_recognised(string text)
    {
        var result = VoiceCommands.Extract(text);

        result.Submit.ShouldBeTrue();
        result.Text.ShouldBe("Send it.");
    }

    [Fact]
    public void The_command_alone_submits_and_types_nothing()
    {
        var result = VoiceCommands.Extract("Press enter.");

        result.Submit.ShouldBeTrue();
        result.Text.ShouldBeEmpty();
    }

    [Fact]
    public void Case_and_trailing_punctuation_do_not_matter()
    {
        VoiceCommands.Extract("Okay. PRESS ENTER!").Submit.ShouldBeTrue();
    }
}

/// <summary>
/// Text that merely mentions the command, which must be typed rather than obeyed.
/// </summary>
/// <remarks>
/// Firing this wrongly sends a half-written message, submits a form, or runs a line in a
/// terminal. Missing it types four extra words. The guards are set for that asymmetry, and this
/// battery is what holds them there.
/// </remarks>
public sealed class VoiceCommandNegativeTests
{
    [Theory]
    // Documentation about pressing Enter. A comma is not enough of a pause.
    [InlineData("To submit the form, press enter")]
    [InlineData("Fill in your name and press enter")]
    [InlineData("Type your password, then press enter")]
    [InlineData("The instructions say press enter")]
    // Connectives keep it prose even after a full stop.
    [InlineData("Do the thing. And press enter.")]
    [InlineData("Almost done. Then press enter.")]
    [InlineData("One more step. Just press enter.")]
    [InlineData("If you're ready. Please press enter.")]
    // Not at the end.
    [InlineData("Press enter to continue reading the document")]
    [InlineData("Press enter, then close the window")]
    // Not the phrase at all.
    [InlineData("Enter the building through the side door")]
    [InlineData("Press the button firmly")]
    [InlineData("")]
    [InlineData("   ")]
    public void The_words_are_typed_rather_than_obeyed(string text)
    {
        var result = VoiceCommands.Extract(text);

        result.Submit.ShouldBeFalse($"\"{text}\" must not press Enter");
        result.Text.ShouldBe(text);
    }

    [Fact]
    public void The_phrase_must_be_spoken_as_one_run()
    {
        // "Press, enter the code" carries the same two words with a pause between them.
        VoiceCommands.Extract("Ready. Press, enter.").Submit.ShouldBeFalse();
    }

    [Fact]
    public void A_command_after_a_comma_is_not_a_command()
    {
        // The single most valuable guard: a comma is how people write an instruction, a full
        // stop is how they finish dictating and issue one.
        VoiceCommands.Extract("Message Dave about the meeting, press enter").Submit.ShouldBeFalse();
    }
}
