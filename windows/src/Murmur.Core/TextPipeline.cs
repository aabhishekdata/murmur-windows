using Murmur.Dictionary;

namespace Murmur.Core;

/// <summary>Which text passes are switched on.</summary>
/// <remarks>
/// Every pass that rewrites the user's words has a switch, and every switch defaults on. The
/// defaults are what makes the feature worth having; the switches are what makes it safe to
/// have, because a pass that guesses wrong about your speech is one you must be able to turn
/// off without abandoning the app.
/// </remarks>
public sealed record TextPipelineOptions
{
    /// <summary>Drop "um", "uh" and the rest.</summary>
    public bool RemoveFillers { get; init; } = true;

    /// <summary>Honour spoken retractions like "wait no".</summary>
    public bool ApplySelfCorrections { get; init; } = true;

    /// <summary>Treat a trailing "press enter" as a command rather than as text.</summary>
    public bool EnterCommand { get; init; } = true;

    /// <summary>Expand snippet triggers.</summary>
    public bool ExpandSnippets { get; init; } = true;

    /// <summary>Turn spoken enumerations into real lists.</summary>
    public bool FormatLists { get; init; } = true;

    /// <summary>The correction dictionary. Always applied; it is the user's own rule set.</summary>
    public IReadOnlyList<DictionaryEntry> Dictionary { get; init; } = [];

    /// <summary>The snippet table.</summary>
    public IReadOnlyList<SnippetEntry> Snippets { get; init; } = [];
}

/// <summary>What the pipeline did.</summary>
/// <param name="Text">The text to type.</param>
/// <param name="Submit">Whether to press Enter after typing it.</param>
/// <param name="Corrections">Dictionary corrections that fired.</param>
/// <param name="Snippets">Snippets that expanded.</param>
/// <param name="FillersRemoved">How many disfluencies were dropped.</param>
/// <param name="SelfCorrections">How many spoken retractions were honoured.</param>
/// <param name="ListStyle">Which list shape, if any, the text was rewritten into.</param>
/// <param name="WholeSnippet">
/// The trigger of the saved prompt the whole utterance matched, or null. When set,
/// <paramref name="Text"/> is that prompt's stored text.
/// </param>
/// <param name="Verbatim">
/// Whether <paramref name="Text"/> must arrive exactly as stored, line breaks and all — true for a
/// saved prompt, which an injector should paste rather than type.
/// </param>
public readonly record struct TextPipelineResult(
    string Text,
    bool Submit,
    IReadOnlyList<AppliedCorrection> Corrections,
    IReadOnlyList<AppliedSnippet> Snippets,
    int FillersRemoved,
    int SelfCorrections,
    ListStyle ListStyle,
    string? WholeSnippet = null,
    bool Verbatim = false);

/// <summary>
/// Everything that happens to transcribed text between the recogniser and the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// Six passes, plus a step that can end the run early, work over the same string, and the order
/// they run in changes the answer. It is set here, once, deliberately, rather than emerging from
/// the order somebody happened to add them to the engine.
/// </para>
/// <para><b>The order, and why each step is where it is:</b></para>
/// <list type="number">
/// <item>
/// <b>Filler removal.</b> First, because a disfluency is noise in the signal rather than
/// content, and every later pass reads better without it. Concretely: "cloud, um, code" only
/// reaches the dictionary as "cloud code" — and therefore only becomes "Claude Code" — if the
/// hesitation is gone before the dictionary looks. The same holds for a snippet trigger or a
/// list marker interrupted by an "uh".
/// </item>
/// <item>
/// <b>Dictionary correction.</b> Second, so every pass after it reasons about the words the
/// user meant rather than the ones the recogniser guessed. It is also the only pass with an
/// explicit user contract — "when you hear X, write Y" — so it runs before anything that could
/// move the text out from under it.
/// </item>
/// <item>
/// <b>Self-correction.</b> Third: it decides what to delete by reading words ("wait no"), so it
/// wants those words spelled correctly, and it must run before snippets so that retracting a
/// trigger — "my address, wait no, my signature" — retracts it before it expands into a
/// paragraph.
/// </item>
/// <item>
/// <b>Command extraction.</b> Fourth, and specifically <i>before</i> expansion. A trailing
/// "press enter" has to be something the <b>user said</b>. If a stored snippet body happened to
/// end with those words, expanding first would let stored text press the Enter key — which,
/// in a chat window, sends a message nobody asked to send.
/// </item>
/// <item>
/// <b>Whole-utterance prompts</b> (4b). After the command, so "review prompt. Press enter."
/// pastes the prompt and sends it; after the dictionary and retractions for the same reasons
/// as snippets. On a match the stored prompt replaces the utterance <i>verbatim</i> and the
/// remaining passes are skipped: a saved prompt is the user's own finished text, and neither a
/// snippet trigger nor a list marker inside it was said.
/// </item>
/// <item>
/// <b>Snippet expansion.</b> Fifth. By now the trigger is spelled correctly, has survived any
/// retraction, and cannot influence the command.
/// </item>
/// <item>
/// <b>List formatting.</b> Last, because it is the only pass that inserts newlines and every
/// other pass matches on flat, single-line text. Running it earlier would hide a trigger or a
/// marker behind a line break.
/// </item>
/// </list>
/// <para>
/// One consequence is worth stating rather than discovering: a snippet whose body is multi-line
/// suppresses list formatting for that whole utterance, because
/// <see cref="ListFormatter"/> refuses to restructure text that is already structured. That is
/// the conservative direction, and mixing a multi-line snippet with a spoken list in one breath
/// is not a real usage pattern.
/// </para>
/// </remarks>
public static class TextPipeline
{
    /// <summary>Runs every enabled pass, in order.</summary>
    /// <param name="text">The recogniser's output, already joined across segments.</param>
    /// <param name="options">Which passes to run and what to run them with.</param>
    /// <returns>The text to type and everything that happened to it.</returns>
    public static TextPipelineResult Run(string text, TextPipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(text))
        {
            return new TextPipelineResult(
                text ?? string.Empty, Submit: false, [], [], 0, 0, ListStyle.None);
        }

        var current = text;

        // 1. Filler removal.
        var fillers = 0;
        if (options.RemoveFillers)
        {
            var filtered = FillerFilter.Analyse(current);
            current = filtered.Text;
            fillers = filtered.Removed;
        }

        // Pure hesitation. Nothing to type, and nothing further to do to it.
        if (string.IsNullOrWhiteSpace(current))
        {
            return new TextPipelineResult(
                string.Empty, Submit: false, [], [], fillers, 0, ListStyle.None);
        }

        // 2. Dictionary correction — unconditional, because it is the user's own rule set.
        var (corrected, applied) = new DictionaryCorrector(options.Dictionary).Apply(current);
        current = corrected;

        // 3. Self-correction.
        var retractions = 0;
        if (options.ApplySelfCorrections)
        {
            var revised = SelfCorrection.Analyse(current);
            current = revised.Text;
            retractions = revised.Applied;
        }

        // 4. Command extraction, before expansion so stored text can never press a key.
        var submit = false;
        if (options.EnterCommand)
        {
            var command = VoiceCommands.Extract(current);
            current = command.Text;
            submit = command.Submit;
        }

        // 4b. A saved prompt said on its own. Pasted exactly as stored; nothing after this
        // touches it.
        if (options.ExpandSnippets && options.Snippets.Count > 0
            && new WholeUtteranceMatcher(options.Snippets).Match(current, allowSubmitPhrase: options.EnterCommand)
                is { } prompt)
        {
            var trigger = prompt.Snippet.Trigger.Trim();
            return new TextPipelineResult(
                prompt.Snippet.Text,
                submit || prompt.Submit,
                applied,
                [new AppliedSnippet(trigger, 1)],
                fillers,
                retractions,
                ListStyle.None,
                WholeSnippet: trigger,
                Verbatim: true);
        }

        // 5. Snippet expansion.
        IReadOnlyList<AppliedSnippet> snippets = [];
        if (options.ExpandSnippets && options.Snippets.Count > 0)
        {
            var expanded = new SnippetExpander(options.Snippets).Expand(current);
            current = expanded.Text;
            snippets = expanded.Applied;
        }

        // 6. Structure, last of all.
        var style = ListStyle.None;
        if (options.FormatLists)
        {
            var formatted = ListFormatter.Analyse(current);
            current = formatted.Text;
            style = formatted.Style;
        }

        return new TextPipelineResult(current, submit, applied, snippets, fillers, retractions, style);
    }
}
