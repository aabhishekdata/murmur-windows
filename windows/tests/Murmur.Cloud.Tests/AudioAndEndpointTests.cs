using System.Buffers.Binary;
using System.Text;
using Murmur.Cloud;
using Shouldly;
using Xunit;

namespace Murmur.Cloud.Tests;

/// <summary>The WAV encoder, the prompt and the endpoint field.</summary>
public sealed class AudioAndEndpointTests
{
    // ---- WAV ----

    [Fact]
    public void The_header_describes_16_kHz_mono_16_bit_PCM()
    {
        var samples = new float[100];
        var bytes = new byte[WavEncoder.ByteCount(samples.Length)];

        WavEncoder.Encode(samples, bytes).ShouldBe(244);

        Encoding.ASCII.GetString(bytes, 0, 4).ShouldBe("RIFF");
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)).ShouldBe(236);
        Encoding.ASCII.GetString(bytes, 8, 4).ShouldBe("WAVE");
        Encoding.ASCII.GetString(bytes, 12, 4).ShouldBe("fmt ");
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16)).ShouldBe(16);
        BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(20)).ShouldBe((short)1, "PCM");
        BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22)).ShouldBe((short)1, "mono");
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)).ShouldBe(16000);
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28)).ShouldBe(32000, "byte rate");
        BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(32)).ShouldBe((short)2, "block align");
        BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34)).ShouldBe((short)16);
        Encoding.ASCII.GetString(bytes, 36, 4).ShouldBe("data");
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)).ShouldBe(200);
    }

    [Fact]
    public void Samples_are_scaled_and_out_of_range_ones_clamped_rather_than_wrapped()
    {
        float[] samples = [0f, 1f, -1f, 0.5f, 1.7f, -3f];
        var bytes = new byte[WavEncoder.ByteCount(samples.Length)];
        WavEncoder.Encode(samples, bytes);

        short At(int i) => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + i * 2));

        At(0).ShouldBe((short)0);
        At(1).ShouldBe(short.MaxValue);
        At(2).ShouldBe((short)-short.MaxValue);
        At(3).ShouldBe((short)16384);
        At(4).ShouldBe(short.MaxValue, "an overshoot clips, it does not wrap to the opposite extreme");
        At(5).ShouldBe((short)-short.MaxValue);
    }

    [Fact]
    public void A_destination_that_is_too_small_is_refused()
    {
        Should.Throw<ArgumentException>(() => WavEncoder.Encode(new float[10], new byte[40]));
    }

    [Fact]
    public void The_pooled_encoding_matches_the_plain_one_and_returns_its_buffer_once()
    {
        var samples = Enumerable.Range(0, 333).Select(i => MathF.Sin(i / 10f)).ToArray();
        var plain = new byte[WavEncoder.ByteCount(samples.Length)];
        WavEncoder.Encode(samples, plain);

        var pooled = WavEncoder.EncodePooled(samples);
        pooled.Memory.ToArray().ShouldBe(plain);
        pooled.Length.ShouldBe(plain.Length);

        pooled.Dispose();
        pooled.Dispose();
        Should.Throw<ObjectDisposedException>(() => pooled.Memory);
    }

    // ---- Prompt ----

    [Fact]
    public void The_prompt_lists_the_dictionary_terms_once_each()
    {
        TranscriptionPrompt.Build(["Kubernetes", " kubernetes ", "", "Claude Code"])
            .ShouldBe("Vocabulary: Kubernetes, Claude Code.");
    }

    [Fact]
    public void No_terms_and_no_hint_means_no_prompt()
    {
        TranscriptionPrompt.Build([]).ShouldBeNull();
        TranscriptionPrompt.Build(null, "  ").ShouldBeNull();
    }

    [Fact]
    public void A_language_hint_comes_with_the_terms()
    {
        TranscriptionPrompt.Build(["Murmur"], "The speaker mixes Hindi and English.")
            .ShouldBe("The speaker mixes Hindi and English. Vocabulary: Murmur.");
        TranscriptionPrompt.Build([], "Hinglish.").ShouldBe("Hinglish.");
    }

    [Fact]
    public void A_long_list_is_cut_at_a_whole_term_within_the_limit()
    {
        var terms = Enumerable.Range(0, 200).Select(i => $"Term{i:000}").ToArray();

        var prompt = TranscriptionPrompt.Build(terms)!;

        prompt.Length.ShouldBeLessThanOrEqualTo(TranscriptionPrompt.MaxLength);
        prompt.ShouldEndWith(".");
        prompt.ShouldStartWith("Vocabulary: Term000, Term001");
        prompt.TrimEnd('.').Split(", ").Last().ShouldMatch(@"^Term\d{3}$", "no term is cut in half");
    }

    // ---- Endpoint ----

    [Theory]
    [InlineData("https://contoso.openai.azure.com/", "https://contoso.openai.azure.com/")]
    [InlineData("contoso.openai.azure.com", "https://contoso.openai.azure.com/")]
    [InlineData("  https://contoso.openai.azure.com  ", "https://contoso.openai.azure.com/")]
    [InlineData(
        "https://contoso.openai.azure.com/openai/deployments/gpt-4o-transcribe/audio/transcriptions?api-version=2025-03-01-preview",
        "https://contoso.openai.azure.com/")]
    [InlineData("https://contoso.cognitiveservices.azure.com/some/path?x=1#y", "https://contoso.cognitiveservices.azure.com/")]
    [InlineData("https://contoso.services.ai.azure.com/api/projects/proj", "https://contoso.services.ai.azure.com/")]
    [InlineData("contoso.services.ai.azure.com", "https://contoso.services.ai.azure.com/")]
    public void A_pasted_Azure_url_is_reduced_to_the_resource(string pasted, string expected)
    {
        AzureEndpoint.TryNormalize(pasted, out var endpoint, out var problem).ShouldBeTrue();

        endpoint!.AbsoluteUri.ShouldBe(expected);
        problem.ShouldBeNull("an Azure host needs no warning");
    }

    [Fact]
    public void A_company_gateway_is_accepted_with_a_warning_and_keeps_its_prefix()
    {
        AzureEndpoint.TryNormalize(
                "https://api.contoso.com/aoai/openai/deployments/x/audio/transcriptions?api-version=1",
                out var endpoint, out var problem)
            .ShouldBeTrue();

        endpoint!.AbsoluteUri.ShouldBe("https://api.contoso.com/aoai");
        problem.ShouldNotBeNull().ShouldContain("api.contoso.com");
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000", "http://127.0.0.1:5000/")]
    [InlineData("http://127.0.0.1:5000/openai/deployments/x/audio/transcriptions?api-version=1", "http://127.0.0.1:5000/")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/")]
    [InlineData("http://[::1]:9000", "http://[::1]:9000/")]
    public void Plain_http_to_loopback_is_accepted_with_a_warning_only_when_the_test_suite_allows_it(string pasted, string expected)
    {
        AzureEndpoint.TryNormalizeCore(pasted, allowLoopbackHttp: false, out _, out var refused).ShouldBeFalse();
        refused.ShouldNotBeNull().ShouldContain("https://");

        AzureEndpoint.TryNormalizeCore(pasted, allowLoopbackHttp: true, out var endpoint, out var problem).ShouldBeTrue();

        endpoint!.AbsoluteUri.ShouldBe(expected);
        problem.ShouldNotBeNull().ShouldContain("local test server");
    }

    [Theory]
    [InlineData("http://contoso.openai.azure.com", "https://")]
    [InlineData("http://api.contoso.com", "https://")]
    [InlineData("http://10.0.0.5:5000", "https://")]
    [InlineData("http://localhost.contoso.com", "https://")]
    [InlineData("http://127.0.0.1.nip.io", "https://")]
    [InlineData("ftp://127.0.0.1", "https://")]
    [InlineData("", "Enter")]
    [InlineData(null, "Enter")]
    [InlineData("https://", "not a URL")]
    public void Unusable_endpoints_are_refused_with_a_reason(string? pasted, string reason)
    {
        AzureEndpoint.TryNormalize(pasted, out var endpoint, out var problem).ShouldBeFalse();

        endpoint.ShouldBeNull();
        problem.ShouldNotBeNull().ShouldContain(reason);

        // None of these is a loopback host, so the test suite's switch makes no difference.
        AzureEndpoint.TryNormalizeCore(pasted, allowLoopbackHttp: true, out _, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://localhost:8080")]
    [InlineData("http://[::1]:9000")]
    public void The_app_refuses_plain_http_even_to_loopback(string pasted)
    {
        AzureEndpoint.AllowLoopbackHttp.ShouldBeFalse("only the end-to-end suite turns it on, in its own process");
        AzureEndpoint.TryNormalize(pasted, out var endpoint, out var problem).ShouldBeFalse();

        endpoint.ShouldBeNull();
        problem.ShouldNotBeNull().ShouldContain("https://");
    }
}
