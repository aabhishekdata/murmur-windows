using System.Text.Json;
using System.Text.Json.Serialization;
using Murmur.Core;
using Murmur.Dictionary;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>
/// Exports the complete cross-platform text contract consumed by the Android/JVM port.
/// Add a case here whenever a pass-level or pipeline test gains a new behavioural input.
/// </summary>
/// <remarks>
/// Version 2 added the <c>wholeUtterance</c> pass, a snippet's <c>match</c>, and a pipeline
/// result's <c>wholeSnippet</c> and <c>verbatim</c>. Every new field is omitted when it has its
/// default value, so a version 1 case reads exactly as it did; <c>shared/README.md</c> asks
/// consumers to skip passes and fields they do not know.
/// </remarks>
public sealed class TextPipelineVectorExportTests
{
    private sealed record Suite(int Version, IReadOnlyList<VectorCase> Cases);
    private sealed record VectorEntry(string Kind, string? Hear, string Write, bool? IsEnabled = null);
    private sealed record VectorSnippet(string Trigger, string Text, bool? IsEnabled = null, string? Match = null);
    private sealed record VectorOptions(
        bool? RemoveFillers = null,
        bool? ApplySelfCorrections = null,
        bool? EnterCommand = null,
        bool? ExpandSnippets = null,
        bool? FormatLists = null);
    private sealed record VectorCorrection(string To, int Count);
    private sealed record VectorAppliedSnippet(string Trigger, int Count);
    private sealed record VectorCase(
        string Name,
        string Pass,
        [property: JsonPropertyName("in")] string Input,
        [property: JsonPropertyName("out")] string Output,
        bool? Submit = null,
        IReadOnlyList<VectorEntry>? Dictionary = null,
        IReadOnlyList<VectorSnippet>? Snippets = null,
        VectorOptions? Options = null,
        int? FillersRemoved = null,
        int? SelfCorrections = null,
        string? ListStyle = null,
        IReadOnlyList<VectorCorrection>? Corrections = null,
        IReadOnlyList<VectorAppliedSnippet>? AppliedSnippets = null,
        string? WholeSnippet = null,
        bool? Verbatim = null);

    /// <summary>The value a whole-utterance prompt carries in <c>match</c>.</summary>
    private const string Whole = "wholeUtterance";

    private const string ReviewBody = "Review this change as a senior engineer.\n"
        + "1. one apples two bananas three oranges\n"
        + "2. Check that cloud code is spelled the way I wrote it, um, here.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    [Fact]
    public void Shared_pipeline_vector_file_is_current()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "shared", "text-pipeline-vectors.json");
        var json = JsonSerializer.Serialize(new Suite(2, BuildCases(root)), JsonOptions) + Environment.NewLine;

        if (Environment.GetEnvironmentVariable("MURMUR_UPDATE_PIPELINE_VECTORS") == "1")
        {
            File.WriteAllText(path, json);
        }

        File.Exists(path).ShouldBeTrue(
            "run with MURMUR_UPDATE_PIPELINE_VECTORS=1 to generate the shared contract");
        File.ReadAllText(path).ShouldBe(json, "the committed vector file is stale");
    }

    private static List<VectorCase> BuildCases(string root)
    {
        var cases = new List<VectorCase>();
        AddDictionaryCases(cases, root);
        AddFillerCases(cases);
        AddSelfCorrectionCases(cases);
        AddCommandCases(cases);
        AddSnippetCases(cases);
        AddListCases(cases);
        AddPipelineCases(cases);

        // Version 2. Last, so every version 1 case keeps its name: the pipeline negatives are
        // numbered by position.
        AddSavedPromptCases(cases);
        cases.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count().ShouldBe(cases.Count);
        cases.Count.ShouldBeGreaterThanOrEqualTo(150, "positive and negative batteries must both be exported");
        return cases;
    }

    private static void AddDictionaryCases(List<VectorCase> cases, string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "shared", "dictionary-test-vectors.json")));

        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var entries = item.GetProperty("entries").EnumerateArray().Select(entry => new VectorEntry(
                entry.GetProperty("kind").GetString()!,
                entry.TryGetProperty("hear", out var hear) ? hear.GetString() : null,
                entry.GetProperty("write").GetString()!,
                entry.TryGetProperty("isEnabled", out var enabled) ? enabled.GetBoolean() : null)).ToArray();
            var dictionary = entries.Select(ToDictionaryEntry).ToArray();
            var input = item.GetProperty("input").GetString()!;
            var expected = item.GetProperty("expected").GetString()!;
            var result = new DictionaryCorrector(dictionary).Apply(input);
            result.Text.ShouldBe(expected, item.GetProperty("name").GetString());

            cases.Add(new VectorCase(
                "dictionary/" + item.GetProperty("name").GetString(),
                "dictionary",
                input,
                result.Text,
                Dictionary: entries,
                Corrections: result.Applied.Select(a => new VectorCorrection(a.To, a.Count)).ToArray()));
        }
    }

    private static void AddFillerCases(List<VectorCase> cases)
    {
        string[] inputs =
        [
            "So um I think we should ship it",
            "Um so uh I think er we should ship it",
            "Well, um, fine.",
            "Um, hello there.",
            "Ummm I think uhhh maybe",
            "That's it um.",
            "Um, uh, er.",
            "UM I think UH so",
            "Ahmed sent the summary",
            "The German erratum was hers",
            "Her umbrella is under there",
            "He erred on the side of caution",
            "Number the columns",
            "The hummus was ahead of its time",
            "Mumbai in the summer",
            "ah-ha, that explains it",
            "uh-huh, go on",
            "mm-hmm",
            "huh, that's odd",
            "Oh, I see",
            "Aha, found it",
            "A perfectly ordinary sentence",
            "",
            "   ",
            "Werner told Hermann there were errors",
            "The uh-oh moment",
            "It was, like, actually fine, you know, basically",
            "hello um there",
        ];
        AddInputs(cases, "filler", inputs, (name, input) =>
        {
            var result = FillerFilter.Analyse(input);
            return new VectorCase(name, "filler", input, result.Text, FillersRemoved: result.Removed);
        });
    }

    private static void AddSelfCorrectionCases(List<VectorCase> cases)
    {
        string[] inputs =
        [
            "Let's meet Tuesday, wait no, Friday.",
            "Let's meet at three on Tuesday, wait no, at four on Friday.",
            "The deadline is Monday, actually no, Wednesday.",
            "Send it to Dave, sorry I meant Steve.",
            "The file is in Documents, I mean Downloads.",
            "Let's meet Tuesday at three. Scratch that, Friday at noon.",
            "Book the room, order the food, and call Dave. Scratch that, cancel it.",
            "Meet Tuesday, wait no, Thursday, wait no, Friday.",
            "Tuesday, wait no, Friday.",
            "I can't wait, no really, it's fine",
            "Please wait. No rush at all.",
            "I actually agree with you",
            "Actually that's a good point",
            "Well actually no one told me about it",
            "But actually no more excuses",
            "Do you know what I mean, Dave?",
            "I mean it.",
            "That is not what I mean.",
            "You know what I mean by that",
            "I meant to call you yesterday",
            "That is not what I meant.",
            "I had to scratch that itch eventually",
            "Scratch that one off the list",
            "No, seriously, that's fine",
            "Everything is fine, I mean",
            "A perfectly ordinary sentence about Tuesday and Friday",
            "",
            "   ",
            "Well, I mean, it depends.",
            "The plan, I mean, of course, was always Friday",
            "Tuesday, wait no, I have completely changed my mind about the whole thing",
            "I said no, wait for me",
            "Tuesday, no wait, Friday.",
            string.Join(", wait no, ", Enumerable.Repeat("Tuesday", 40)),
        ];
        AddInputs(cases, "self-correction", inputs, (name, input) =>
        {
            var result = SelfCorrection.Analyse(input);
            return new VectorCase(name, "selfCorrection", input, result.Text, SelfCorrections: result.Applied);
        });
    }

    private static void AddCommandCases(List<VectorCase> cases)
    {
        string[] inputs =
        [
            "Tell Dave I'll be ten minutes late. Press enter.",
            "Send it. Press enter.",
            "Send it. Press return.",
            "Send it. Hit enter.",
            "Send it. Hit return.",
            "Press enter.",
            "Okay. PRESS ENTER!",
            "To submit the form, press enter",
            "Fill in your name and press enter",
            "Type your password, then press enter",
            "The instructions say press enter",
            "Do the thing. And press enter.",
            "Almost done. Then press enter.",
            "One more step. Just press enter.",
            "If you're ready. Please press enter.",
            "Press enter to continue reading the document",
            "Press enter, then close the window",
            "Enter the building through the side door",
            "Press the button firmly",
            "",
            "   ",
            "Ready. Press, enter.",
            "Message Dave about the meeting, press enter",
        ];
        AddInputs(cases, "command", inputs, (name, input) =>
        {
            var result = VoiceCommands.Extract(input);
            return new VectorCase(name, "command", input, result.Text, Submit: result.Submit);
        });
    }

    private static void AddSnippetCases(List<VectorCase> cases)
    {
        AddSnippet(cases, "basic", "Send it to my address please.",
            [new("my address", "12 Bridge Street")]);
        AddSnippet(cases, "multi-line", "my signature", [new("my signature", "Best,\nPat")]);
        AddSnippet(cases, "longest", "Use my address in full here",
            [new("my address", "SHORT"), new("my address in full", "LONG")]);
        AddSnippet(cases, "substring-negative", "The design is significant", [new("sig", "SIGNATURE")]);
        AddSnippet(cases, "fence-negative", "The address bar is fine", [new("addr", "12 Bridge Street")]);
        AddSnippet(cases, "fence-positive", "Type addr here", [new("addr", "12 Bridge Street")]);
        AddSnippet(cases, "glued", "standup", [new("stand up", "Yesterday: shipped the bar.")]);
        AddSnippet(cases, "hyphenated", "stand-up", [new("stand up", "Yesterday: shipped the bar.")]);
        AddSnippet(cases, "literal-replacement", "price note", [new("price note", "Costs $1 and $& more")]);
        AddSnippet(cases, "disabled", "my address", [new("my address", "12 Bridge Street", false)]);
        AddSnippet(cases, "short-a", "a", [new("a", "NOPE")]);
        AddSnippet(cases, "short-hi", "hi", [new("hi", "NOPE")]);
        AddSnippet(cases, "empty-body", "my address", [new("my address", "")]);
        AddSnippet(cases, "repeated", "my address and my address", [new("my address", "HERE")]);
        AddSnippet(cases, "no-trigger", "An ordinary sentence about somewhere else entirely.",
            [new("my address", "HERE")]);
    }

    private static void AddSavedPromptCases(List<VectorCase> cases)
    {
        AddSnippet(cases, "whole-utterance-excluded", "Please use review prompt here",
            [new("review prompt", "NOPE", Match: Whole)]);
        AddSnippet(cases, "whole-utterance-excluded-alone", "review prompt",
            [new("review prompt", "NOPE", Match: Whole)]);

        VectorSnippet[] prompt = [new("review prompt", ReviewBody, Match: Whole)];
        AddPipeline(cases, "whole-match", "Review prompt.", snippets: prompt);
        AddPipeline(cases, "whole-after-fillers", "Um, review, uh, prompt.", snippets: prompt);
        AddPipeline(cases, "whole-after-retraction", "My address, wait no, review prompt.",
            snippets: [.. prompt, new("my address", "12 Bridge Street")]);
        AddPipeline(cases, "whole-after-dictionary", "Review prom.",
            dictionary: [new("correction", "prom", "prompt")], snippets: prompt);
        AddPipeline(cases, "whole-no-match", "Please review prompt.", snippets: prompt);
        AddPipeline(cases, "whole-press-enter", "Review prompt. Press enter.", snippets: prompt);
        AddPipeline(cases, "whole-press-enter-unpunctuated", "review prompt press enter", snippets: prompt);
        AddPipeline(cases, "whole-press-enter-command-off", "review prompt press enter", snippets: prompt,
            options: new(EnterCommand: false));
        AddPipeline(cases, "whole-body-untouched", "review prompt",
            dictionary: [new("correction", "cloud code", "Claude Code")],
            snippets: [.. prompt, new("bananas", "INLINE")]);
        AddPipeline(cases, "whole-body-not-a-command", "send prompt",
            snippets: [new("send prompt", "Ship it. Press enter.", Match: Whole)]);
        AddPipeline(cases, "whole-disabled", "review prompt",
            snippets: [new("review prompt", ReviewBody, IsEnabled: false, Match: Whole)]);
        AddPipeline(cases, "whole-snippets-off", "review prompt", snippets: prompt,
            options: new(ExpandSnippets: false));
        AddPipeline(cases, "whole-beside-inline", "Use my address. Review prompt.",
            snippets: [.. prompt, new("my address", "12 Bridge Street")]);

        VectorSnippet[] prompts =
        [
            new("review prompt", ReviewBody, Match: Whole),
            new("stand up notes", "Yesterday:\nToday:\nBlockers:", Match: Whole),
            new("caf\u00e9 order", "One flat white, please.", Match: Whole),
            new("inline one", "INLINE"),
            new("off prompt", "DISABLED", IsEnabled: false, Match: Whole),
            new("ok", "TOO SHORT", Match: Whole),
        ];

        string[] inputs =
        [
            "review prompt",
            "Review prompt.",
            "REVIEW PROMPT!",
            "  review   prompt  ",
            "Review, prompt?",
            "\"Review prompt\"",
            "review-prompt",
            "reviewprompt",
            "Review prompt. Press enter.",
            "review prompt press enter",
            "Review prompt, hit return",
            "review prompt press",
            "please review prompt",
            "review prompt please",
            "the review prompt is long",
            "review prompts",
            "Stand-up notes.",
            "standup notes",
            "Caf\u00e9 order.",
            "Cafe\u0301 order",
            "caf\u00e9 order \U0001F642",
            "inline one",
            "off prompt",
            "ok",
            "press enter",
            "",
            "   ",
        ];

        for (var index = 0; index < inputs.Length; index++)
        {
            AddWhole(cases, $"wholeUtterance/{index + 1:D3}", inputs[index], prompts, allowSubmit: true);
        }

        AddWhole(cases, "wholeUtterance/no-submit-phrase", "review prompt press enter", prompts, allowSubmit: false);
        AddWhole(cases, "wholeUtterance/first-duplicate-wins", "review prompt",
            [new("review prompt", "FIRST", Match: Whole), new("Review  Prompt", "SECOND", Match: Whole)],
            allowSubmit: true);
    }

    private static void AddWhole(
        List<VectorCase> cases, string name, string input, IReadOnlyList<VectorSnippet> snippets, bool allowSubmit)
    {
        var match = new WholeUtteranceMatcher(snippets.Select(ToSnippetEntry)).Match(input, allowSubmit);
        cases.Add(new VectorCase(
            name,
            "wholeUtterance",
            input,
            match?.Snippet.Text ?? input,
            Submit: match?.Submit ?? false,
            Snippets: snippets,
            Options: allowSubmit ? null : new VectorOptions(EnterCommand: false),
            WholeSnippet: match?.Snippet.Trigger.Trim()));
    }

    private static void AddListCases(List<VectorCase> cases)
    {
        string[] inputs =
        [
            "one apples two bananas three oranges",
            "1. Apples 2. Bananas 3. Oranges",
            "One, apples. Two, bananas. Three, oranges.",
            "First, call the bank. Second, email Dave. Third, book the flight.",
            "Number one, milk. Number two, eggs.",
            "One, milk. Two, eggs.",
            "Here's my shopping list. One, milk. Two, eggs. Three, bread.",
            "One, milk. Two, eggs. Then I went home.",
            "First, I was scared. Second, I didn't want to hurt her.",
            "1st, milk. 2nd, eggs. 3rd, bread.",
            "One, milk. Two, eggs. Three, bread. Four, butter.",
            "One, iPhone. Two, iPad.",
            "milk, eggs, bread, butter",
            "milk, eggs, bread, and butter.",
            "olive oil, whole milk, brown bread",
            "milk, eggs",
            "Here's what I need: milk, eggs, bread",
            "Here's the thing: it was cold, it was dark, it was wet",
            "After a great deal of thought and rather more coffee than was strictly sensible I concluded the following: milk, eggs, bread",
            "extra virgin olive oil, large red onions, plain white bread",
            "one two three four five six, a, b",
            "Note: this is the thing: milk, eggs, bread",
            "the first time I met her was in Paris",
            "The first thing I noticed was the second door on the left.",
            "On the one hand it is cheap, on the other hand it is slow",
            "I only have one question",
            "He was one of two children.",
            "I need one apple two oranges",
            "Pick one or two and let me know",
            "two coffees please",
            "There were two options; one was cheaper.",
            "one two three",
            "First of all, thanks for coming. Second of all, sorry I'm late.",
            "It was cold, dark, and wet.",
            "I went to the store, bought some milk, and came home.",
            "Yeah, sure, okay.",
            "Hi John, how are you doing today?",
            "",
            "   ",
            "Just a normal sentence with no numbers in it at all.",
            "One, I went to the shop and it was closed and then it started raining hard. Two, eggs.",
            "1. Apples\n2. Bananas",
            "One, milk. Three, bread.",
            "Two, bananas. One, apples.",
            "One, milk. Second, eggs.",
            "Fifty, sixty, seventy",
        ];
        AddInputs(cases, "list", inputs, (name, input) =>
        {
            var result = ListFormatter.Analyse(input);
            return new VectorCase(name, "list", input, result.Text,
                ListStyle: result.Style.ToString().ToLowerInvariant());
        });
    }

    private static void AddPipelineCases(List<VectorCase> cases)
    {
        AddPipeline(cases, "order-fillers-dictionary", "I use cloud um code every day",
            dictionary: [new("correction", "cloud code", "Claude Code")]);
        AddPipeline(cases, "punctuated-filler-limit", "I use cloud, um, code every day",
            dictionary: [new("correction", "cloud code", "Claude Code")]);
        AddPipeline(cases, "order-dictionary-self", "Meet Tuesday, weight no, Friday.",
            dictionary: [new("correction", "weight no", "wait no")]);
        AddPipeline(cases, "symmetric-bound", "Send it to Claude Code, wait no, Vercel.");
        AddPipeline(cases, "order-self-snippet", "Use my address, wait no, my signature.",
            snippets: [new("my address", "12 Bridge Street"), new("my signature", "Best, Pat")]);
        AddPipeline(cases, "order-command-snippet", "Use my sign off",
            snippets: [new("my sign off", "Kind regards. Press enter.")]);
        AddPipeline(cases, "command-with-snippet", "Use my sign off. Press enter.",
            snippets: [new("my sign off", "Kind regards")]);
        AddPipeline(cases, "order-list-last", "Um, one apples, two bananas, three oranges.");
        AddPipeline(cases, "multi-line-snippet", "one my signature two bananas",
            snippets: [new("my signature", "Best,\nPat")]);
        AddPipeline(cases, "everything", "Um, email steev, wait no, Dave, using my sign off. Press enter.",
            dictionary: [new("correction", "steev", "Steve")],
            snippets: [new("my sign off", "Kind regards")]);

        const string sample = "Um, one apples two bananas. Press enter.";
        AddPipeline(cases, "toggle-fillers", sample, options: new(RemoveFillers: false));
        AddPipeline(cases, "toggle-lists", sample, options: new(FormatLists: false));
        AddPipeline(cases, "toggle-command", sample, options: new(EnterCommand: false));
        AddPipeline(cases, "toggle-self", "Meet Tuesday, wait no, Friday.",
            options: new(ApplySelfCorrections: false));
        AddPipeline(cases, "toggle-snippets", "Use my address.",
            snippets: [new("my address", "12 Bridge Street")], options: new(ExpandSnippets: false));
        AddPipeline(cases, "dictionary-always", "cloud code",
            dictionary: [new("correction", "cloud code", "Claude Code")],
            options: new(false, false, false, false, false));

        string[] negatives =
        [
            "The first time I met her was in Paris, and I only have one question about it.",
            "I actually agree, and I meant to call you about the umbrella.",
            "Ahmed said there were errors in the German summary.",
            "To submit the form, press enter, then wait for the confirmation.",
            "He was one of two children, which is not what I mean by a large family.",
            "It was cold, dark, and wet, so we went home.",
            "Please wait. No rush at all.",
            "I had to scratch that itch, and honestly I can't wait, no really.",
            "Um, uh, er.",
            "Press enter.",
            "",
            "   ",
        ];
        foreach (var input in negatives) AddPipeline(cases, "negative-" + cases.Count, input);
    }

    private static void AddSnippet(
        List<VectorCase> cases, string name, string input, IReadOnlyList<VectorSnippet> snippets)
    {
        var entries = snippets.Select(ToSnippetEntry).ToArray();
        var result = new SnippetExpander(entries).Expand(input);
        cases.Add(new VectorCase(
            "snippet/" + name,
            "snippet",
            input,
            result.Text,
            Snippets: snippets,
            AppliedSnippets: result.Applied.Select(a => new VectorAppliedSnippet(a.Trigger, a.Count)).ToArray()));
    }

    private static void AddPipeline(
        List<VectorCase> cases,
        string name,
        string input,
        IReadOnlyList<VectorEntry>? dictionary = null,
        IReadOnlyList<VectorSnippet>? snippets = null,
        VectorOptions? options = null)
    {
        dictionary ??= [];
        snippets ??= [];
        options ??= new();
        var result = TextPipeline.Run(input, new TextPipelineOptions
        {
            RemoveFillers = options.RemoveFillers ?? true,
            ApplySelfCorrections = options.ApplySelfCorrections ?? true,
            EnterCommand = options.EnterCommand ?? true,
            ExpandSnippets = options.ExpandSnippets ?? true,
            FormatLists = options.FormatLists ?? true,
            Dictionary = dictionary.Select(ToDictionaryEntry).ToArray(),
            Snippets = snippets.Select(ToSnippetEntry).ToArray(),
        });
        cases.Add(new VectorCase(
            "pipeline/" + name,
            "pipeline",
            input,
            result.Text,
            result.Submit,
            dictionary,
            snippets,
            options,
            result.FillersRemoved,
            result.SelfCorrections,
            result.ListStyle.ToString().ToLowerInvariant(),
            result.Corrections.Select(a => new VectorCorrection(a.To, a.Count)).ToArray(),
            result.Snippets.Select(a => new VectorAppliedSnippet(a.Trigger, a.Count)).ToArray(),
            result.WholeSnippet,
            result.Verbatim ? true : null));
    }

    private static void AddInputs(
        List<VectorCase> cases,
        string prefix,
        string[] inputs,
        Func<string, string, VectorCase> make)
    {
        for (var index = 0; index < inputs.Length; index++)
        {
            cases.Add(make($"{prefix}/{index + 1:D3}", inputs[index]));
        }
    }

    private static DictionaryEntry ToDictionaryEntry(VectorEntry entry) => new()
    {
        Kind = string.Equals(entry.Kind, "correction", StringComparison.OrdinalIgnoreCase)
            ? EntryKind.Correction
            : EntryKind.Term,
        Hear = entry.Hear ?? string.Empty,
        Write = entry.Write,
        IsEnabled = entry.IsEnabled ?? true,
    };

    private static SnippetEntry ToSnippetEntry(VectorSnippet entry) => new()
    {
        Trigger = entry.Trigger,
        Text = entry.Text,
        IsEnabled = entry.IsEnabled ?? true,
        Match = string.Equals(entry.Match, Whole, StringComparison.Ordinal)
            ? SnippetMatch.WholeUtterance
            : SnippetMatch.Inline,
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "shared", "dictionary-test-vectors.json")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Murmur repository root.");
    }
}
