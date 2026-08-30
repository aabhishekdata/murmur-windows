using Murmur.Core;
using Murmur.Dictionary;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>
/// The passes running together, in the order the pipeline fixes.
/// </summary>
/// <remarks>
/// Six passes now rewrite the same string and the order changes the answer. Testing each one
/// alone proves it works; these prove they work <i>together</i>, and each test here is tied to
/// one specific ordering decision that would silently produce different text if reversed.
/// </remarks>
public sealed class TextPipelineOrderTests
{
    private static TextPipelineOptions With(
        IEnumerable<DictionaryEntry>? dictionary = null,
        IEnumerable<SnippetEntry>? snippets = null) => new()
        {
            Dictionary = dictionary?.ToList() ?? [],
            Snippets = snippets?.ToList() ?? [],
        };

    [Fact]
    public void Fillers_go_before_the_dictionary_so_a_hesitation_cannot_hide_a_correction()
    {
        // Ordering decision 1 → 2. Reversed, the dictionary sees "cloud um code" and its rule
        // for "cloud code" never fires, so the user's own correction silently stops working
        // whenever they hesitate mid-phrase.
        var result = TextPipeline.Run(
            "I use cloud um code every day",
            With(dictionary: [DictionaryEntry.Correction("cloud code", "Claude Code")]));

        result.Text.ShouldBe("I use Claude Code every day");
        result.FillersRemoved.ShouldBe(1);
        result.Corrections.ShouldHaveSingleItem();
    }

    [Fact]
    public void A_hesitation_the_recogniser_fenced_with_commas_keeps_one_of_them()
    {
        // The honest limit of the pass above. When the recogniser writes "cloud, um, code" the
        // commas are real punctuation as far as any rule can tell — "So, um, I think" has the
        // same shape and its comma is legitimate — so one survives and the dictionary rule does
        // not fire. Removing it would be guessing, and this pass does not guess.
        var result = TextPipeline.Run(
            "I use cloud, um, code every day",
            With(dictionary: [DictionaryEntry.Correction("cloud code", "Claude Code")]));

        result.FillersRemoved.ShouldBe(1);
        result.Text.ShouldBe("I use cloud, code every day");
        result.Corrections.ShouldBeEmpty();
    }

    [Fact]
    public void The_dictionary_goes_before_self_correction_so_retractions_read_corrected_words()
    {
        // Ordering decision 2 → 3, and it is load-bearing: the retraction marker itself can be
        // what the recogniser got wrong. With the dictionary first, the user's rule repairs
        // "weight no" into "wait no" and the retraction is then seen. Reversed, self-correction
        // looks at "weight no", finds no marker, and both halves get typed.
        var result = TextPipeline.Run(
            "Meet Tuesday, weight no, Friday.",
            With(dictionary: [DictionaryEntry.Correction("weight no", "wait no")]));

        result.Corrections.ShouldHaveSingleItem();
        result.SelfCorrections.ShouldBe(1);
        result.Text.ShouldBe("Meet Friday.");
    }

    [Fact]
    public void A_retraction_only_removes_as_many_words_as_it_adds()
    {
        // The documented bound on symmetric substitution, stated as a test rather than left to
        // be discovered: replacing a two-word phrase with a one-word one removes one word. The
        // rule cannot eat a sentence, and that guarantee is worth more than handling every
        // restatement shape.
        var result = TextPipeline.Run("Send it to Claude Code, wait no, Vercel.", With());

        result.Text.ShouldBe("Send it to Claude Vercel.");
        result.SelfCorrections.ShouldBe(1);
    }

    [Fact]
    public void Self_correction_goes_before_snippets_so_a_retracted_trigger_never_expands()
    {
        // Ordering decision 3 → 5. Reversed, retracting a trigger would leave a whole
        // paragraph of stored text sitting in the sentence with nothing to remove it.
        var result = TextPipeline.Run(
            "Use my address, wait no, my signature.",
            With(snippets:
            [
                new SnippetEntry { Trigger = "my address", Text = "12 Bridge Street" },
                new SnippetEntry { Trigger = "my signature", Text = "Best, Pat" },
            ]));

        result.Text.ShouldBe("Use Best, Pat.");
        result.Snippets.ShouldHaveSingleItem().Trigger.ShouldBe("my signature");
    }

    [Fact]
    public void Commands_are_extracted_before_snippets_so_stored_text_can_never_press_a_key()
    {
        // Ordering decision 4 → 5, and the one with the worst failure mode. A snippet body
        // ending in "press enter" must not submit anything: the user did not say those words.
        var result = TextPipeline.Run(
            "Use my sign off",
            With(snippets:
            [
                new SnippetEntry { Trigger = "my sign off", Text = "Kind regards. Press enter." },
            ]));

        result.Submit.ShouldBeFalse("stored text must never press a key");
        result.Text.ShouldBe("Use Kind regards. Press enter.");
    }

    [Fact]
    public void A_command_the_user_actually_said_still_submits_alongside_a_snippet()
    {
        var result = TextPipeline.Run(
            "Use my sign off. Press enter.",
            With(snippets: [new SnippetEntry { Trigger = "my sign off", Text = "Kind regards" }]));

        result.Submit.ShouldBeTrue();
        result.Text.ShouldBe("Use Kind regards.");
    }

    [Fact]
    public void List_formatting_runs_last_so_earlier_passes_see_flat_text()
    {
        // Ordering decision 6. List formatting is the only pass that inserts newlines, and
        // every other pass matches on single-line text.
        var result = TextPipeline.Run(
            "Um, one apples, two bananas, three oranges.",
            With());

        result.Text.ShouldBe("1. Apples\n2. Bananas\n3. Oranges");
        result.ListStyle.ShouldBe(ListStyle.Numbered);
        result.FillersRemoved.ShouldBe(1);
    }

    [Fact]
    public void A_multi_line_snippet_suppresses_list_formatting_for_that_utterance()
    {
        // The documented consequence of snippets running before lists. Conservative by design:
        // ListFormatter refuses to restructure text that is already structured.
        var result = TextPipeline.Run(
            "one my signature two bananas",
            With(snippets: [new SnippetEntry { Trigger = "my signature", Text = "Best,\nPat" }]));

        result.ListStyle.ShouldBe(ListStyle.None);
        result.Text.ShouldContain("Best,\nPat");
    }

    [Fact]
    public void Everything_at_once()
    {
        // One utterance through all six passes: a hesitation, a dictionary correction, a
        // retraction, a snippet and a trailing command.
        var result = TextPipeline.Run(
            "Um, email steev, wait no, Dave, using my sign off. Press enter.",
            With(
                dictionary: [DictionaryEntry.Correction("steev", "Steve")],
                snippets: [new SnippetEntry { Trigger = "my sign off", Text = "Kind regards" }]));

        result.FillersRemoved.ShouldBe(1);
        result.Corrections.ShouldHaveSingleItem();
        result.SelfCorrections.ShouldBe(1);
        result.Snippets.ShouldHaveSingleItem();
        result.Submit.ShouldBeTrue();
        result.Text.ShouldBe("Email Dave, using Kind regards.");
    }
}

/// <summary>Every pass can be switched off, and switching one off changes only that pass.</summary>
public sealed class TextPipelineToggleTests
{
    private const string Sample = "Um, one apples two bananas. Press enter.";

    [Fact]
    public void Everything_is_on_by_default()
    {
        var options = new TextPipelineOptions();

        options.RemoveFillers.ShouldBeTrue();
        options.ApplySelfCorrections.ShouldBeTrue();
        options.EnterCommand.ShouldBeTrue();
        options.ExpandSnippets.ShouldBeTrue();
        options.FormatLists.ShouldBeTrue();
    }

    [Fact]
    public void Turning_off_filler_removal_leaves_the_fillers_in()
    {
        var result = TextPipeline.Run(Sample, new TextPipelineOptions { RemoveFillers = false });

        result.FillersRemoved.ShouldBe(0);
        result.Text.ShouldContain("Um");
    }

    [Fact]
    public void Turning_off_list_formatting_leaves_one_flat_line()
    {
        var result = TextPipeline.Run(Sample, new TextPipelineOptions { FormatLists = false });

        result.ListStyle.ShouldBe(ListStyle.None);
        result.Text.ShouldNotContain("\n");
    }

    [Fact]
    public void Turning_off_the_enter_command_types_the_words_instead()
    {
        var result = TextPipeline.Run(Sample, new TextPipelineOptions { EnterCommand = false });

        result.Submit.ShouldBeFalse();
        result.Text.ShouldContain("Press enter", Case.Insensitive);
    }

    [Fact]
    public void Turning_off_self_correction_keeps_both_halves_of_the_retraction()
    {
        var result = TextPipeline.Run(
            "Meet Tuesday, wait no, Friday.",
            new TextPipelineOptions { ApplySelfCorrections = false });

        result.SelfCorrections.ShouldBe(0);
        result.Text.ShouldBe("Meet Tuesday, wait no, Friday.");
    }

    [Fact]
    public void Turning_off_snippets_types_the_trigger_as_spoken()
    {
        var result = TextPipeline.Run("Use my address.", new TextPipelineOptions
        {
            ExpandSnippets = false,
            Snippets = [new SnippetEntry { Trigger = "my address", Text = "12 Bridge Street" }],
        });

        result.Snippets.ShouldBeEmpty();
        result.Text.ShouldBe("Use my address.");
    }

    [Fact]
    public void The_dictionary_has_no_switch_because_it_is_the_users_own_rule_set()
    {
        // Every heuristic pass can be turned off. The dictionary is not a heuristic — it is
        // exactly what the user typed in — so it always applies.
        var result = TextPipeline.Run("cloud code", new TextPipelineOptions
        {
            RemoveFillers = false,
            ApplySelfCorrections = false,
            EnterCommand = false,
            ExpandSnippets = false,
            FormatLists = false,
            Dictionary = [DictionaryEntry.Correction("cloud code", "Claude Code")],
        });

        result.Text.ShouldBe("Claude Code");
    }

    [Fact]
    public void Settings_map_onto_the_pipeline_switch_for_switch()
    {
        var data = new SettingsData
        {
            RemoveFillers = false,
            ApplySelfCorrections = true,
            EnterCommand = false,
            ExpandSnippets = true,
            FormatLists = false,
        };

        var options = data.ToOptions();

        options.RemoveFillers.ShouldBeFalse();
        options.ApplySelfCorrections.ShouldBeTrue();
        options.EnterCommand.ShouldBeFalse();
        options.ExpandSnippets.ShouldBeTrue();
        options.FormatLists.ShouldBeFalse();
    }

    [Fact]
    public void A_new_install_has_every_clean_up_switched_on()
    {
        var data = new SettingsData();

        data.RemoveFillers.ShouldBeTrue();
        data.ApplySelfCorrections.ShouldBeTrue();
        data.EnterCommand.ShouldBeTrue();
        data.ExpandSnippets.ShouldBeTrue();
        data.FormatLists.ShouldBeTrue();
        data.MicrophoneId.ShouldBeNull();
    }
}

/// <summary>Text that must come out of the whole pipeline exactly as it went in.</summary>
/// <remarks>
/// Six passes each with their own guards can still, between them, mangle a sentence none of
/// them would have touched alone. This is the battery that watches for that.
/// </remarks>
public sealed class TextPipelineNegativeTests
{
    [Theory]
    [InlineData("The first time I met her was in Paris, and I only have one question about it.")]
    [InlineData("I actually agree, and I meant to call you about the umbrella.")]
    [InlineData("Ahmed said there were errors in the German summary.")]
    [InlineData("To submit the form, press enter, then wait for the confirmation.")]
    [InlineData("He was one of two children, which is not what I mean by a large family.")]
    [InlineData("It was cold, dark, and wet, so we went home.")]
    [InlineData("Please wait. No rush at all.")]
    [InlineData("I had to scratch that itch, and honestly I can't wait, no really.")]
    public void Ordinary_prose_survives_every_pass_untouched(string text)
    {
        var result = TextPipeline.Run(text, new TextPipelineOptions());

        result.Text.ShouldBe(text);
        result.Submit.ShouldBeFalse();
        result.FillersRemoved.ShouldBe(0);
        result.SelfCorrections.ShouldBe(0);
        result.ListStyle.ShouldBe(ListStyle.None);
    }

    [Fact]
    public void An_utterance_of_pure_hesitation_types_nothing_and_submits_nothing()
    {
        var result = TextPipeline.Run("Um, uh, er.", new TextPipelineOptions());

        result.Text.ShouldBeEmpty();
        result.Submit.ShouldBeFalse();
    }

    [Fact]
    public void A_bare_command_types_nothing_but_still_submits()
    {
        var result = TextPipeline.Run("Press enter.", new TextPipelineOptions());

        result.Text.ShouldBeEmpty();
        result.Submit.ShouldBeTrue();
    }

    [Fact]
    public void Empty_input_is_handled_without_throwing()
    {
        TextPipeline.Run("", new TextPipelineOptions()).Text.ShouldBeEmpty();
        TextPipeline.Run("   ", new TextPipelineOptions()).Submit.ShouldBeFalse();
    }
}
