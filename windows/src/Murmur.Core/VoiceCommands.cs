namespace Murmur.Core;

/// <summary>What a trailing voice command asked for.</summary>
/// <param name="Text">The text with the command phrase removed.</param>
/// <param name="Submit">Whether to press Enter once the text has been typed.</param>
public readonly record struct VoiceCommandResult(string Text, bool Submit);

/// <summary>
/// Strips a trailing spoken command and reports what it asked for.
/// </summary>
/// <remarks>
/// <para>
/// "Tell Dave I'll be ten minutes late. Press enter." should type the message and then submit
/// it, not type the words "press enter" into the end of it. Only one command exists — pressing
/// Enter — because it is the only one whose mechanism (a synthetic keystroke through the
/// existing injector) already exists and needs no new permission or API.
/// </para>
/// <para>
/// <b>The failure mode here is asymmetric, and the guards are set accordingly.</b> Missing the
/// command types four extra words the user then deletes. Firing it wrongly sends a half-finished
/// message in Slack, submits a form, or runs a command in a terminal. So the phrase only counts
/// when all four of these hold:
/// </para>
/// <list type="number">
/// <item><b>It is the last thing said.</b> Nothing may follow it but punctuation.</item>
/// <item><b>It stands as its own sentence</b>, or it is the entire utterance. This is the guard
/// that matters most: it is what separates "Send the report. Press enter." from "To submit the
/// form, press enter" — the second is documentation, and the comma is not enough of a
/// pause.</item>
/// <item><b>The word in front of it is not a connective.</b> ". And press enter." is still
/// prose, even with the full stop.</item>
/// <item><b>Something is left to type</b>, unless the command was the whole utterance — in
/// which case saying "press enter" on its own is exactly the intended use.</item>
/// </list>
/// <para>
/// The strictness is deliberate and one-directional: when this is wrong it is wrong in the
/// direction of typing four words too many.
/// </para>
/// </remarks>
public static class VoiceCommands
{
    /// <summary>
    /// The phrases that mean "submit", as bare word sequences.
    /// </summary>
    /// <remarks>
    /// Deliberately not "send it", "go", "submit" or "done": each of those is something a
    /// person says as ordinary content, and none of them is worth the risk of an accidental
    /// send.
    /// </remarks>
    internal static readonly string[][] SubmitPhrases =
    [
        ["press", "enter"],
        ["press", "return"],
        ["hit", "enter"],
        ["hit", "return"],
        ["new", "paragraph", "enter"],
    ];

    /// <summary>
    /// Words that leave the phrase reading as prose even after a full stop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sentence-boundary requirement does nearly all the work — every plausible false
    /// positive ("to submit the form, press enter", "fill in your name and press enter") is
    /// already refused because a comma or a conjunction is not a full stop. This list covers
    /// only the residue: a recogniser that puts a full stop where a comma belonged, mid-way
    /// through an instruction.
    /// </para>
    /// <para>
    /// Deliberately connectives and instruction verbs <b>only</b>. Pronouns are not here, and
    /// must not be added: "Send it. Press enter." is the single most ordinary use of this
    /// feature there is, and blocking "it" would break it.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> BlockedBeforeCommand =
        new(StringComparer.Ordinal)
        {
            "and", "then", "or", "so", "but", "just", "please",
            "say", "says", "saying", "type", "typing", "click", "clicking", "key", "button",
        };

    /// <summary>Removes a trailing command phrase from <paramref name="text"/>.</summary>
    /// <param name="text">Transcribed text.</param>
    /// <returns>The text to type, and whether Enter should follow it.</returns>
    public static VoiceCommandResult Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new VoiceCommandResult(text, Submit: false);

        var tokens = SpeechText.Tokenize(text);
        if (tokens.Count == 0) return new VoiceCommandResult(text, Submit: false);

        foreach (var phrase in SubmitPhrases)
        {
            var start = tokens.Count - phrase.Length;
            if (start < 0) continue;

            if (!EndsWith(tokens, start, phrase)) continue;

            // Guard 2 and 3: the phrase has to stand apart from what came before it.
            if (start > 0)
            {
                var previous = tokens[start - 1];
                if (!previous.EndsSentence) continue;
                if (BlockedBeforeCommand.Contains(previous.Word)) continue;
            }

            var remaining = SpeechText.Join(tokens, 0, start);

            // Guard 4: the whole utterance being the command is the intended bare use; a
            // command with nothing left after it in any other shape is not reachable, but the
            // check documents the intent.
            if (start > 0 && remaining.Length == 0) continue;

            return new VoiceCommandResult(remaining, Submit: true);
        }

        return new VoiceCommandResult(text, Submit: false);
    }

    private static bool EndsWith(List<SpokenToken> tokens, int start, string[] phrase)
    {
        for (var i = 0; i < phrase.Length; i++)
        {
            if (!string.Equals(tokens[start + i].Word, phrase[i], StringComparison.Ordinal))
            {
                return false;
            }

            // The phrase must be spoken as one run. "Press, enter the code" is not a command.
            if (i < phrase.Length - 1 && tokens[start + i].EndsClause) return false;
        }

        return true;
    }
}
