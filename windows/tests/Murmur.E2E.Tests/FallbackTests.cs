using Murmur.Abstractions;
using Murmur.Core;
using Shouldly;
using Xunit;

namespace Murmur.E2E.Tests;

/// <summary>
/// What happens when the cloud fails, against a real HTTP endpoint: which failures Parakeet
/// covers for, and which are shown to the user instead.
/// </summary>
/// <remarks>
/// The privacy contract in AGENTS.md: only network failures, timeouts and 408/429/5xx fall back;
/// 400/401/403/404 are reported, and Azure-only mode never falls back at all.
/// </remarks>
public sealed class FallbackTests
{
    private const string Key = DictationTests.Key;

    private static Task<MockAzureServer> StartAsync() =>
        MockAzureServer.StartAsync(Key, Pipeline.AccurateDeployment, Pipeline.FastDeployment);

    private static Pipeline Create(MockAzureServer mock, EngineMode mode = EngineMode.AzureWithParakeetFallback, string key = Key, Func<SettingsData, SettingsData>? more = null) =>
        Pipeline.Create(data => (more ?? (d => d))(Pipeline.PointedAt(data, mock, mode)), key);

    private static void FellBackToParakeet(Dictation dictation, string scenario)
    {
        var wer = Expect.Delivered(dictation);
        dictation.Result!.Engine.ShouldBe(TranscriberKind.Parakeet);
        dictation.Result.FellBack.ShouldBeTrue();
        Expect.Latency($"Fallback: {scenario}", dictation, wer);
    }

    [Fact]
    public async Task Service_errors_on_every_attempt_fall_back_to_Parakeet()
    {
        E2E.Require();
        await using var mock = (await StartAsync()).Enqueue(new MockFailure(503), times: 3);
        await using var pipeline = Create(mock);

        var dictation = await pipeline.DictateAsync(Clip.Load("sentence"));

        FellBackToParakeet(dictation, "503 x3");
        mock.Requests.Count.ShouldBe(3, "three attempts, then Parakeet");
        mock.Requests.ShouldAllBe(r => r.Status == 503);
        pipeline.Router.Fallback.IsCircuitOpen.ShouldBeTrue("the cloud is skipped for a minute after failing");

        // With the circuit open the next dictation goes straight to Parakeet, sending nothing.
        var next = await pipeline.DictateAsync(Clip.Load("hello"));
        FellBackToParakeet(next, "circuit open");
        mock.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Service_errors_that_clear_within_the_retries_are_answered_by_Azure()
    {
        E2E.Require();
        await using var mock = (await StartAsync()).Enqueue(new MockFailure(503), times: 2);
        await using var pipeline = Create(mock);

        var dictation = await pipeline.DictateAsync(Clip.Load("sentence"));

        var wer = Expect.Delivered(dictation);
        dictation.Result!.Engine.ShouldBe(TranscriberKind.Azure);
        dictation.Result.FellBack.ShouldBeFalse();
        mock.Requests.Select(r => r.Status).ShouldBe([503, 503, 200]);
        pipeline.ParakeetBuilds.ShouldBe(0);
        Expect.Latency("Azure after 503 x2", dictation, wer, mock.Requests[^1].Transcription);
    }

    [Fact]
    public async Task A_rate_limit_is_waited_out_as_the_service_asks()
    {
        E2E.Require();
        await using var mock = (await StartAsync()).Enqueue(new MockFailure(429, "Rate limit exceeded.", RetryAfterMs: 750));
        await using var pipeline = Create(mock);

        var dictation = await pipeline.DictateAsync(Clip.Load("hello"));

        Expect.Delivered(dictation);
        dictation.Result!.Engine.ShouldBe(TranscriberKind.Azure);
        mock.Requests.Select(r => r.Status).ShouldBe([429, 200]);
        (mock.Requests[1].At - mock.Requests[0].At).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(700), "retry-after-ms is honoured");
    }

    [Fact]
    public async Task A_service_that_never_answers_times_out_and_Parakeet_answers()
    {
        E2E.Require();
        await using var mock = (await StartAsync()).Enqueue(MockFailure.Hang, times: 5);
        await using var pipeline = Create(mock, more: data => data with { CloudRequestTimeoutSeconds = 5 });

        var dictation = await pipeline.DictateAsync(Clip.Load("sentence"));

        FellBackToParakeet(dictation, "hang, 5 s timeout");
        dictation.Wall.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(4.5), "the cloud had its whole budget first");
        mock.Requests.Count.ShouldBe(1, "a timeout uses the budget up; there is no time left to retry");
    }

    [Fact]
    public async Task With_the_network_down_Parakeet_answers()
    {
        E2E.Require();
        await using var mock = await StartAsync();
        await using var pipeline = Create(mock);

        // The endpoint stops listening: every connection is refused, as with no network.
        await mock.StopAsync();
        var dictation = await pipeline.DictateAsync(Clip.Load("sentence"));

        FellBackToParakeet(dictation, "network down");
        dictation.Fault.ShouldBeNull();
    }

    [Fact]
    public async Task A_rejected_key_is_shown_not_papered_over_with_Parakeet()
    {
        E2E.Require();
        await using var mock = await StartAsync();
        await using var pipeline = Create(mock, key: "e2e-wrong-key-00000000");

        var dictation = await pipeline.DictateAsync(Clip.Load("hello"));

        dictation.Result.ShouldBeNull("nothing is typed when the key is refused");
        var fault = dictation.Fault.ShouldBeOfType<TranscriptionException>();
        fault.Failure.ShouldBe(TranscriptionFailure.Unauthorized);
        fault.StatusCode.ShouldBe(401);
        fault.ToString().ShouldNotContain("e2e-wrong-key", customMessage: "the key is never in an exception");

        mock.Requests.ShouldHaveSingleItem("a 401 is not retried").Status.ShouldBe(401);
        pipeline.ParakeetBuilds.ShouldBe(0, "fallback never hides a broken setup");
        pipeline.Key.State.ShouldBe(CloudKeyState.Rejected, "the bar says \"Key rejected\"");
        dictation.Keyboard.ShouldNotContain(e => e == "paste" || e.StartsWith("type:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(403, TranscriptionFailure.Forbidden, "Public access is disabled.")]
    [InlineData(404, TranscriptionFailure.NotFound, "The API deployment for this resource does not exist.")]
    [InlineData(400, TranscriptionFailure.BadRequest, "Audio file might be corrupted or unsupported.")]
    public async Task A_setup_error_is_reported_and_not_retried_or_covered_for(int status, TranscriptionFailure failure, string message)
    {
        E2E.Require();
        await using var mock = (await StartAsync()).Enqueue(new MockFailure(status, message));
        await using var pipeline = Create(mock);

        var dictation = await pipeline.DictateAsync(Clip.Load("hello"));

        dictation.Result.ShouldBeNull();
        var fault = dictation.Fault.ShouldBeOfType<TranscriptionException>();
        fault.Failure.ShouldBe(failure);
        fault.StatusCode.ShouldBe(status);
        if (status is 400 or 403) fault.Message.ShouldContain(message, customMessage: "Azure's own words are passed on");

        mock.Requests.ShouldHaveSingleItem();
        pipeline.ParakeetBuilds.ShouldBe(0);
        pipeline.Key.State.ShouldNotBe(CloudKeyState.Rejected, "only a 401 says the key is wrong");
    }

    [Fact]
    public async Task A_deployment_name_that_does_not_exist_is_a_404_from_the_endpoint_itself()
    {
        E2E.Require();
        await using var mock = await StartAsync();
        await using var pipeline = Create(mock, more: data => data with { AzureDeploymentAccurate = "no-such-deployment" });

        var dictation = await pipeline.DictateAsync(Clip.Load("hello"));

        dictation.Fault.ShouldBeOfType<TranscriptionException>().Failure.ShouldBe(TranscriptionFailure.NotFound);
        mock.Requests.ShouldHaveSingleItem().Deployment.ShouldBe("no-such-deployment");
    }

    [Fact]
    public async Task Azure_only_mode_never_falls_back_even_when_the_service_is_down()
    {
        E2E.Require();
        await using var mock = (await StartAsync()).Enqueue(new MockFailure(503), times: 3);
        await using var pipeline = Create(mock, EngineMode.Azure);

        var dictation = await pipeline.DictateAsync(Clip.Load("hello"));

        dictation.Result.ShouldBeNull();
        dictation.Fault.ShouldBeOfType<TranscriptionException>().Failure.ShouldBe(TranscriptionFailure.ServerError);
        pipeline.ParakeetBuilds.ShouldBe(0, "Azure-only mode does not load Parakeet, ever");
    }

    [Fact]
    public async Task The_long_clip_falls_back_whole()
    {
        E2E.Require();
        await using var mock = (await StartAsync()).Enqueue(new MockFailure(500), times: 3);
        await using var pipeline = Create(mock);

        var dictation = await pipeline.DictateAsync(Clip.Load("long"));

        FellBackToParakeet(dictation, "500 x3, long clip");
    }
}
