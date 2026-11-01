using System.Globalization;
using Murmur.Abstractions;
using Murmur.Cloud;
using Shouldly;

namespace Murmur.E2E.Tests;

/// <summary>What each clip should come out as, and the latency report.</summary>
internal static class Expect
{
    private static readonly string[] Fillers = ["um", "uh", "erm", "uhm", "hmm"];

    /// <summary>Checks that what reached the focused app is what the clip asked for.</summary>
    /// <param name="dictation">The dictation.</param>
    /// <returns>The word error rate of the delivered text; 0 for a saved prompt, which must be exact.</returns>
    public static double Delivered(Dictation dictation)
    {
        dictation.Fault.ShouldBeNull($"{dictation.Clip.Name}: the dictation failed");
        var result = dictation.Result.ShouldNotBeNull($"{dictation.Clip.Name}: nothing was transcribed");

        switch (dictation.Clip.Name)
        {
            case "trigger":
            case "trigger-enter":
                // Said on its own, the trigger pastes the stored prompt exactly, line breaks and all.
                result.Text.ShouldBe(Pipeline.PromptBody, $"heard \"{dictation.Typed}\"");
                dictation.Pasted.ShouldNotBeNull("a saved prompt is pasted, however short").ReplaceLineEndings("\n")
                    .ShouldBe(Pipeline.PromptBody);
                dictation.Typed.ShouldBeEmpty("nothing of the trigger is typed");
                if (dictation.Clip.Name == "trigger-enter")
                {
                    dictation.PressedEnterAfterText.ShouldBeTrue("\"press enter\" sends, after the paste");
                }
                else
                {
                    dictation.PressedEnter.ShouldBeFalse();
                }

                return 0;

            case "filler":
            {
                var words = WordErrorRate.Words(dictation.Delivered);
                words.Intersect(Fillers).ShouldBeEmpty($"fillers are removed: \"{dictation.Delivered}\"");

                var meant = string.Join(' ', WordErrorRate.Words(dictation.Clip.Spoken).Except(Fillers));
                return Within(meant, dictation);
            }

            default:
                dictation.PressedEnter.ShouldBeFalse();
                return Within(dictation.Clip.Spoken, dictation);
        }
    }

    private static double Within(string said, Dictation dictation)
    {
        var wer = WordErrorRate.Of(said, dictation.Delivered);
        wer.ShouldBeLessThanOrEqualTo(
            WordErrorRate.Tolerance,
            $"{dictation.Clip.Name}: said \"{said}\", delivered \"{dictation.Delivered}\"");
        return wer;
    }

    /// <summary>Checks that a request reached the stand-in exactly as Azure expects it.</summary>
    /// <param name="request">The request.</param>
    /// <param name="key">The key that should have been sent.</param>
    /// <param name="deployment">The deployment it should have gone to.</param>
    public static void WellFormed(MockRequest request, string key, string deployment)
    {
        request.Problems.ShouldBeEmpty();
        request.Deployment.ShouldBe(deployment);
        request.Model.ShouldBe(deployment, "the deployment name is also sent as the model");
        request.ApiKey.ShouldBe(key);
        request.ApiVersion.ShouldBe(AzureTranscriptionOptions.DefaultApiVersion);
        request.ResponseFormat.ShouldBe("json");
        request.ChunkingStrategy.ShouldBe("auto");
        request.FileName.ShouldBe("audio.wav");
        request.Audio.ShouldNotBeNull().IsPcm16Mono16k.ShouldBeTrue();

        // The dictionary's terms travel in the prompt to bias the model toward their spelling.
        var prompt = request.Prompt.ShouldNotBeNull("the dictionary terms are sent as the prompt");
        foreach (var term in Pipeline.DictionaryTerms) prompt.ShouldContain(term);
    }

    /// <summary>Adds one line to <c>latency.tsv</c>.</summary>
    /// <param name="engine">Which engine was chosen, and how.</param>
    /// <param name="dictation">The dictation.</param>
    /// <param name="wer">Its word error rate.</param>
    /// <param name="server">For the cloud, how long the stand-in's own transcription took.</param>
    public static void Latency(string engine, Dictation dictation, double wer, TimeSpan? server = null)
    {
        var result = dictation.Result;
        E2E.Report(
            "latency.tsv",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.Now:s}\t{engine}\t{dictation.Clip.Name}\t{dictation.Clip.Duration.TotalSeconds:0.0} s audio"
                + $"\t{result?.ProcessingTime.TotalMilliseconds ?? -1:0} ms to text"
                + $"\t{dictation.Wall.TotalMilliseconds:0} ms to delivered"
                + $"\t{(server is { } s ? $"{s.TotalMilliseconds:0} ms server" : "-")}"
                + $"\t{result?.Engine ?? TranscriberKind.Unknown}{(result?.FellBack == true ? " (fell back)" : string.Empty)}"
                + $"\tWER {wer:0.00}\t\"{dictation.Delivered.ReplaceLineEndings(" ")}\""));
    }
}
