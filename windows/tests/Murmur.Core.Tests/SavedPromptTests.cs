using System.Text.Json;
using Murmur.Abstractions;
using Murmur.Core;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>Saved prompts: a trigger said on its own, pasted verbatim.</summary>
public sealed class SavedPromptTests
{
    private const string Body = "Review this change as a senior engineer.\n"
        + "1. one apples two bananas three oranges\n"
        + "2. Check that cloud code is spelled the way I wrote it, um, here.";

    private static SnippetEntry Prompt(string trigger, string text = Body, bool enabled = true) =>
        new() { Trigger = trigger, Text = text, IsEnabled = enabled, Match = SnippetMatch.WholeUtterance };

    private static TextPipelineResult Run(string input, params SnippetEntry[] snippets) =>
        TextPipeline.Run(input, new TextPipelineOptions { Snippets = snippets });

    [Theory]
    [InlineData("review prompt", "review prompt")]
    [InlineData("Review Prompt.", "review prompt")]
    [InlineData("  review \t  prompt \n", "review prompt")]
    [InlineData("Review, prompt?!", "review prompt")]
    [InlineData("\"Review prompt\"", "review prompt")]
    [InlineData("review-prompt", "review prompt")]
    [InlineData("review \u2014 prompt", "review prompt")]
    [InlineData("it's my $5 #1 prompt", "its my 5 1 prompt")]
    [InlineData("Cafe\u0301 order", "caf\u00e9 order")]
    [InlineData("caf\u00e9 order \U0001F642", "caf\u00e9 order")]
    [InlineData("...", "")]
    [InlineData("", "")]
    public void Normalisation_strips_what_a_recogniser_varies(string input, string expected) =>
        WholeUtteranceMatcher.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("review prompt")]
    [InlineData("Review prompt.")]
    [InlineData("REVIEW-PROMPT")]
    [InlineData("reviewprompt")]
    [InlineData("Review, prompt!")]
    public void The_trigger_said_alone_matches(string said)
    {
        var match = new WholeUtteranceMatcher([Prompt("review prompt")]).Match(said, allowSubmitPhrase: true);

        match.ShouldNotBeNull();
        match.Value.Snippet.Text.ShouldBe(Body);
        match.Value.Submit.ShouldBeFalse();
    }

    [Theory]
    [InlineData("please review prompt")]
    [InlineData("review prompt please")]
    [InlineData("the review prompt is long")]
    [InlineData("review prompts")]
    [InlineData("review")]
    public void The_trigger_inside_a_sentence_does_not(string said) =>
        new WholeUtteranceMatcher([Prompt("review prompt")]).Match(said, allowSubmitPhrase: true).ShouldBeNull();

    [Fact]
    public void Disabled_inline_empty_and_short_prompts_never_match()
    {
        var matcher = new WholeUtteranceMatcher(
        [
            Prompt("off prompt", enabled: false),
            new SnippetEntry { Trigger = "inline one", Text = "INLINE" },
            Prompt("empty one", text: string.Empty),
            Prompt("ok"),
        ]);

        matcher.IsEmpty.ShouldBeTrue();
        matcher.Match("off prompt", allowSubmitPhrase: true).ShouldBeNull();
        matcher.Match("inline one", allowSubmitPhrase: true).ShouldBeNull();
        matcher.Match("ok", allowSubmitPhrase: true).ShouldBeNull();
    }

    [Theory]
    [InlineData("review prompt press enter")]
    [InlineData("Review prompt, hit return.")]
    [InlineData("review prompt new paragraph enter")]
    public void A_trailing_submit_phrase_pastes_then_submits(string said)
    {
        var match = new WholeUtteranceMatcher([Prompt("review prompt")]).Match(said, allowSubmitPhrase: true);

        match.ShouldNotBeNull();
        match.Value.Submit.ShouldBeTrue();
    }

    [Fact]
    public void The_submit_phrase_is_text_when_the_command_is_switched_off() =>
        new WholeUtteranceMatcher([Prompt("review prompt")])
            .Match("review prompt press enter", allowSubmitPhrase: false).ShouldBeNull();

    [Fact]
    public void The_pipeline_pastes_the_body_exactly_and_skips_the_later_passes()
    {
        // The body holds a dictionary trigger, an inline snippet trigger, a spoken list and a
        // filler. None of them was said; all of them must survive untouched.
        var result = TextPipeline.Run("Um, review prompt.", new TextPipelineOptions
        {
            Dictionary = [Murmur.Dictionary.DictionaryEntry.Correction("cloud code", "Claude Code")],
            Snippets = [Prompt("review prompt"), new SnippetEntry { Trigger = "bananas", Text = "INLINE" }],
        });

        result.Text.ShouldBe(Body);
        result.Verbatim.ShouldBeTrue();
        result.WholeSnippet.ShouldBe("review prompt");
        result.ListStyle.ShouldBe(ListStyle.None);
        result.Submit.ShouldBeFalse();
        result.FillersRemoved.ShouldBe(1);
        result.Snippets.ShouldHaveSingleItem().Trigger.ShouldBe("review prompt");
    }

    [Fact]
    public void A_prompt_body_ending_in_press_enter_does_not_submit()
    {
        var result = Run("send prompt", Prompt("send prompt", "Ship it. Press enter."));

        result.Text.ShouldBe("Ship it. Press enter.");
        result.Submit.ShouldBeFalse("stored text must never press a key");
    }

    [Theory]
    [InlineData("Review prompt. Press enter.")]
    [InlineData("review prompt press enter")]
    public void Saying_press_enter_after_the_trigger_submits(string said)
    {
        var result = Run(said, Prompt("review prompt"));

        result.Text.ShouldBe(Body);
        result.Submit.ShouldBeTrue();
    }

    [Fact]
    public void Without_a_match_the_pipeline_behaves_as_before()
    {
        var result = Run("Please review prompt.", Prompt("review prompt"));

        result.Text.ShouldBe("Please review prompt.");
        result.Verbatim.ShouldBeFalse();
        result.WholeSnippet.ShouldBeNull();
    }

    [Fact]
    public void Switching_snippets_off_switches_prompts_off_too()
    {
        var result = TextPipeline.Run("review prompt", new TextPipelineOptions
        {
            ExpandSnippets = false,
            Snippets = [Prompt("review prompt")],
        });

        result.Text.ShouldBe("review prompt");
        result.Verbatim.ShouldBeFalse();
    }

    [Fact]
    public void The_inline_expander_ignores_prompts()
    {
        var expander = new SnippetExpander([Prompt("review prompt", "NOPE")]);

        expander.IsEmpty.ShouldBeTrue();
        expander.Expand("Please use review prompt here").Text.ShouldBe("Please use review prompt here");
    }

    [Fact]
    public void A_snippets_file_from_before_prompts_loads_as_inline_with_no_shortcut()
    {
        var path = Path.Combine(Path.GetTempPath(), $"murmur-snippets-{Guid.NewGuid():N}.jsonl");
        try
        {
            // Exactly what the previous version wrote.
            File.WriteAllText(path,
                "{\"Id\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"Trigger\":\"my address\","
                + "\"Text\":\"12 Bridge Street\",\"IsEnabled\":true}\n");

            var entry = new SnippetStore(path).Entries.ShouldHaveSingleItem();

            entry.Match.ShouldBe(SnippetMatch.Inline);
            entry.Hotkey.ShouldBeNull();
            entry.Trigger.ShouldBe("my address");
            new SnippetExpander([entry]).Expand("Send it to my address").Text
                .ShouldBe("Send it to 12 Bridge Street");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_prompt_round_trips_with_its_match_and_shortcut()
    {
        var path = Path.Combine(Path.GetTempPath(), $"murmur-snippets-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new SnippetStore(path);
            store.Add(Prompt("review prompt") with { Hotkey = HotkeyChords.Parse("Ctrl+Alt+1") });

            var line = File.ReadAllLines(path).ShouldHaveSingleItem();
            line.ShouldContain("\"Match\":\"WholeUtterance\"");
            line.ShouldContain("\"Hotkey\":\"Ctrl+Alt+1\"");

            var reopened = new SnippetStore(path).Entries.ShouldHaveSingleItem();
            reopened.Match.ShouldBe(SnippetMatch.WholeUtterance);
            reopened.Hotkey.ShouldBe(new HotkeyChord(HotkeyModifiers.Control | HotkeyModifiers.Alt, '1'));
            reopened.Text.ShouldBe(Body);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_hand_edited_shortcut_that_does_not_parse_costs_only_the_shortcut()
    {
        var json = "{\"Trigger\":\"review prompt\",\"Text\":\"x\",\"Match\":\"WholeUtterance\",\"Hotkey\":\"Ctrl+Banana\"}";

        var entry = JsonSerializer.Deserialize(json, SnippetJsonContext.Default.SnippetEntry);

        entry.ShouldNotBeNull();
        entry.Hotkey.ShouldBeNull();
        entry.Match.ShouldBe(SnippetMatch.WholeUtterance);
    }

    [Fact]
    public void A_common_word_prompt_gets_its_own_warning()
    {
        var warning = SnippetWarning.Check(Prompt("thanks")).ShouldHaveSingleItem();
        warning.Message.ShouldContain("say just that word");
    }
}

/// <summary>Key combinations as text.</summary>
public sealed class HotkeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Alt+1", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x31)]
    [InlineData("ctrl + alt + 1", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x31)]
    [InlineData("Control+Shift+P", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x50)]
    [InlineData("Win+Alt+F9", HotkeyModifiers.Windows | HotkeyModifiers.Alt, 0x78)]
    [InlineData("Ctrl+Num7", HotkeyModifiers.Control, 0x67)]
    [InlineData("Ctrl+Alt+=", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0xBB)]
    [InlineData("Ctrl+PageDown", HotkeyModifiers.Control, 0x22)]
    [InlineData("F13", HotkeyModifiers.None, 0x7C)]
    public void Text_parses(string text, HotkeyModifiers modifiers, int virtualKey) =>
        HotkeyChords.Parse(text).ShouldBe(new HotkeyChord(modifiers, virtualKey));

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Hyper+1")]
    [InlineData("Ctrl+Ctrl+1")]
    [InlineData("Ctrl+Alt")]
    public void Nonsense_does_not(string text)
    {
        HotkeyChords.TryParse(text, out _).ShouldBeFalse();
        Should.Throw<FormatException>(() => HotkeyChords.Parse(text));
    }

    [Fact]
    public void Formatting_puts_modifiers_in_a_fixed_order_and_round_trips()
    {
        var chord = new HotkeyChord(
            HotkeyModifiers.Windows | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Control, 'K');

        var text = HotkeyChords.Format(chord);

        text.ShouldBe("Ctrl+Alt+Shift+Win+K");
        HotkeyChords.Parse(text).ShouldBe(chord);
    }

    [Theory]
    [InlineData("Ctrl+Alt+1", true)]
    [InlineData("Alt+F4", true)]
    [InlineData("F13", true)]
    [InlineData("F5", false)]
    [InlineData("Shift+A", false)]
    [InlineData("Space", false)]
    public void A_shortcut_must_not_steal_typing(string text, bool usable) =>
        (HotkeyChords.Problem(HotkeyChords.Parse(text)) is null).ShouldBe(usable);
}
