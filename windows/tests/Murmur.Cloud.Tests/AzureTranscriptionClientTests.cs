using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using Murmur.Abstractions;
using Murmur.Cloud;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.Cloud.Tests;

/// <summary>The request Azure receives, and what is made of its answer.</summary>
public sealed class AzureTranscriptionClientTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static readonly AzureTranscriptionOptions Options = new()
    {
        Endpoint = new Uri("https://contoso.openai.azure.com"),
        Deployment = "gpt-4o-transcribe",
    };

    /// <summary>Jitter fixed at the midpoint, so a backoff is exactly its doubling step.</summary>
    private sealed class MidpointRandom : Random
    {
        public override double NextDouble() => 0.5;
    }

    private sealed class Harness
    {
        public Harness(StubHttpMessageHandler handler)
        {
            Handler = handler;
            Client = new AzureTranscriptionClient(
                new HttpClient(handler),
                Time,
                new MidpointRandom(),
                (wait, _) =>
                {
                    Waits.Add(wait);
                    Time.Advance(wait);
                    return Task.CompletedTask;
                });
        }

        public StubHttpMessageHandler Handler { get; }

        public ManualTimeProvider Time { get; } = new();

        public List<TimeSpan> Waits { get; } = [];

        public AzureTranscriptionClient Client { get; }

        public Task<AzureTranscription> RunAsync(
            AzureTranscriptionOptions? options = null,
            float[]? samples = null,
            IReadOnlyList<string>? bias = null,
            CancellationToken cancellationToken = default) =>
            Client.TranscribeAsync(
                options ?? Options, Key, samples ?? new float[1600], bias ?? [],
                cancellationToken: cancellationToken);
    }

    // ---- The request ----

    [Fact]
    public async Task The_request_goes_to_the_deployment_with_the_key_in_the_api_key_header_only()
    {
        var harness = new Harness(new StubHttpMessageHandler().RespondText("hello"));

        await harness.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        var request = harness.Handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri!.AbsoluteUri.ShouldBe(
            "https://contoso.openai.azure.com/openai/deployments/gpt-4o-transcribe/audio/transcriptions"
            + "?api-version=2025-03-01-preview");
        request.Headers["api-key"].ShouldBe(Key);
        request.Headers.ContainsKey("Authorization").ShouldBeFalse("Azure keys go in api-key, never a bearer token");
        request.Uri.AbsoluteUri.ShouldNotContain(Key);
    }

    [Fact]
    public void A_deployment_name_is_escaped_into_the_path()
    {
        var options = Options with { Deployment = "my dep/../x?y", ApiVersion = "2025-04-01-preview" };

        options.TranscriptionUri.AbsoluteUri.ShouldBe(
            "https://contoso.openai.azure.com/openai/deployments/my%20dep%2F..%2Fx%3Fy/audio/transcriptions"
            + "?api-version=2025-04-01-preview");
    }

    [Fact]
    public void A_gateway_path_is_kept_in_front_of_openai()
    {
        var options = Options with { Endpoint = new Uri("https://gateway.contoso.com/aoai") };

        options.TranscriptionUri.AbsolutePath.ShouldBe(
            "/aoai/openai/deployments/gpt-4o-transcribe/audio/transcriptions");
    }

    [Fact]
    public async Task The_form_carries_the_audio_and_the_fields_Azure_expects()
    {
        var harness = new Harness(new StubHttpMessageHandler().RespondText("hello"));

        await harness.RunAsync(bias: ["Kubernetes", "Claude Code"], cancellationToken: TestContext.Current.CancellationToken);

        var request = harness.Handler.Requests.ShouldHaveSingleItem();
        request.Parts.Select(p => p.Name).ShouldBe(
            ["file", "model", "response_format", "chunking_strategy", "prompt"]);

        var file = request.Part("file")!;
        file.FileName.ShouldBe("audio.wav");
        file.ContentType.ShouldBe("audio/wav");
        file.Body.Length.ShouldBe(44 + 1600 * 2);

        request.Part("model")!.Text.ShouldBe("gpt-4o-transcribe");
        request.Part("response_format")!.Text.ShouldBe("json");
        request.Part("chunking_strategy")!.Text.ShouldBe("auto");
        request.Part("prompt")!.Text.ShouldBe("Vocabulary: Kubernetes, Claude Code.");
        request.Part("language").ShouldBeNull("the model detects the language unless told");
    }

    [Fact]
    public async Task A_language_is_sent_when_chosen_and_no_prompt_when_there_is_nothing_to_say()
    {
        var harness = new Harness(new StubHttpMessageHandler().RespondText("namaste"));

        await harness.RunAsync(Options with { Language = "hi" }, cancellationToken: TestContext.Current.CancellationToken);

        var request = harness.Handler.Requests.ShouldHaveSingleItem();
        request.Part("language")!.Text.ShouldBe("hi");
        request.Part("prompt").ShouldBeNull();
    }

    [Fact]
    public async Task The_answer_is_the_text_trimmed_and_a_missing_text_is_empty()
    {
        var harness = new Harness(new StubHttpMessageHandler()
            .RespondText("  Hello there. ", ("x-ms-region", "Sweden Central"))
            .Respond(HttpStatusCode.OK, "{}"));

        var first = await harness.RunAsync(cancellationToken: TestContext.Current.CancellationToken);
        first.Text.ShouldBe("Hello there.");
        first.Region.ShouldBe("Sweden Central");
        first.Attempts.ShouldBe(1);

        (await harness.RunAsync(cancellationToken: TestContext.Current.CancellationToken)).Text.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task An_answer_that_is_not_json_is_a_failure_not_a_transcript()
    {
        var harness = new Harness(new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "<html>proxy</html>"));

        (await Should.ThrowAsync<TranscriptionException>(() => harness.RunAsync()))
            .Failure.ShouldBe(TranscriptionFailure.Unknown);
    }

    // ---- The size guard ----

    [Fact]
    public async Task A_recording_over_25_MB_is_refused_before_anything_is_sent()
    {
        var harness = new Harness(new StubHttpMessageHandler().RespondText("never"));
        var tooLong = new float[(int)((AzureTranscriptionClient.MaxUploadBytes - 44) / 2) + 1];

        var failure = await Should.ThrowAsync<TranscriptionException>(() => harness.RunAsync(samples: tooLong));

        failure.Failure.ShouldBe(TranscriptionFailure.PayloadTooLarge);
        failure.IsTransient.ShouldBeFalse();
        harness.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_minute_of_audio_fits_comfortably()
    {
        var harness = new Harness(new StubHttpMessageHandler().RespondText("ok"));

        await harness.RunAsync(samples: new float[60 * 16000], cancellationToken: TestContext.Current.CancellationToken);

        harness.Handler.Requests.ShouldHaveSingleItem().Part("file")!.Body.Length.ShouldBe(1_920_044);
    }

    // ---- Errors and retries ----

    public static TheoryData<HttpStatusCode, TranscriptionFailure, bool> Statuses() => new()
    {
        { HttpStatusCode.BadRequest, TranscriptionFailure.BadRequest, false },
        { HttpStatusCode.Unauthorized, TranscriptionFailure.Unauthorized, false },
        { HttpStatusCode.Forbidden, TranscriptionFailure.Forbidden, false },
        { HttpStatusCode.NotFound, TranscriptionFailure.NotFound, false },
        { HttpStatusCode.ProxyAuthenticationRequired, TranscriptionFailure.ProxyAuthenticationRequired, false },
        { HttpStatusCode.RequestEntityTooLarge, TranscriptionFailure.PayloadTooLarge, false },
        { HttpStatusCode.RequestTimeout, TranscriptionFailure.Timeout, true },
        { HttpStatusCode.TooManyRequests, TranscriptionFailure.RateLimited, true },
        { HttpStatusCode.InternalServerError, TranscriptionFailure.ServerError, true },
        { HttpStatusCode.BadGateway, TranscriptionFailure.ServerError, true },
        { HttpStatusCode.ServiceUnavailable, TranscriptionFailure.ServerError, true },
        { HttpStatusCode.GatewayTimeout, TranscriptionFailure.ServerError, true },
        { HttpStatusCode.NotImplemented, TranscriptionFailure.ServerError, true },
        { (HttpStatusCode)520, TranscriptionFailure.ServerError, true },
        { HttpStatusCode.Conflict, TranscriptionFailure.Unknown, false },
        { HttpStatusCode.Found, TranscriptionFailure.Unknown, false },
        { HttpStatusCode.PermanentRedirect, TranscriptionFailure.Unknown, false },
    };

    [Theory]
    [MemberData(nameof(Statuses))]
    public async Task Each_status_maps_to_its_failure_and_only_transient_ones_are_retried(
        HttpStatusCode status, TranscriptionFailure expected, bool retried)
    {
        var harness = new Harness(new StubHttpMessageHandler().Respond(status));

        var failure = await Should.ThrowAsync<TranscriptionException>(() => harness.RunAsync());

        failure.Failure.ShouldBe(expected);
        failure.StatusCode.ShouldBe((int)status);
        failure.IsTransient.ShouldBe(retried);
        harness.Handler.Requests.Count.ShouldBe(retried ? AzureTranscriptionClient.DefaultMaxAttempts : 1);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_the_key_never_reaches_another_host()
    {
        AzureHttp.CreateHandler().AllowAutoRedirect.ShouldBeFalse();

        var harness = new Harness(new StubHttpMessageHandler().Then((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://elsewhere.example.com/collect");
            return Task.FromResult(response);
        }));

        var failure = await Should.ThrowAsync<TranscriptionException>(() => harness.RunAsync());

        failure.Message.ShouldContain("redirected (307)");
        failure.Message.ShouldContain("final URL");
        harness.Handler.Requests.ShouldHaveSingleItem().Uri!.Host.ShouldBe("contoso.openai.azure.com");
    }

    [Fact]
    public async Task A_transient_failure_followed_by_success_is_a_success()
    {
        var harness = new Harness(new StubHttpMessageHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.TooManyRequests)
            .RespondText("third time"));

        var result = await harness.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.Text.ShouldBe("third time");
        result.Attempts.ShouldBe(3);
        harness.Waits.ShouldBe([TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)],
            "exponential backoff, at the jitter's midpoint");
    }

    [Fact]
    public async Task Retry_after_ms_wins_over_Retry_After()
    {
        var harness = new Harness(new StubHttpMessageHandler()
            .Respond(HttpStatusCode.TooManyRequests, null, ("retry-after-ms", "1250"), ("Retry-After", "5"))
            .RespondText("ok"));

        await harness.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        harness.Waits.ShouldBe([TimeSpan.FromMilliseconds(1250)]);
    }

    [Fact]
    public async Task Retry_After_in_seconds_is_honoured()
    {
        var harness = new Harness(new StubHttpMessageHandler()
            .Respond(HttpStatusCode.ServiceUnavailable, null, ("Retry-After", "3"))
            .RespondText("ok"));

        await harness.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        harness.Waits.ShouldBe([TimeSpan.FromSeconds(3)]);
    }

    [Fact]
    public async Task Retry_After_as_a_date_is_measured_from_now()
    {
        var handler = new StubHttpMessageHandler();
        var harness = new Harness(handler);
        var when = harness.Time.GetUtcNow().AddSeconds(4).ToString("R", CultureInfo.InvariantCulture);
        handler.Respond(HttpStatusCode.TooManyRequests, null, ("Retry-After", when)).RespondText("ok");

        await harness.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        harness.Waits.ShouldBe([TimeSpan.FromSeconds(4)]);
    }

    [Fact]
    public async Task A_long_retry_after_is_capped_at_eight_seconds()
    {
        var harness = new Harness(new StubHttpMessageHandler()
            .Respond(HttpStatusCode.TooManyRequests, null, ("Retry-After", "20"))
            .RespondText("ok"));

        await harness.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        harness.Waits.ShouldBe([AzureTranscriptionClient.MaxRetryWait]);
    }

    [Fact]
    public async Task No_wait_is_started_that_would_outlast_the_budget()
    {
        var harness = new Harness(new StubHttpMessageHandler()
            .Respond(HttpStatusCode.TooManyRequests, null, ("Retry-After", "8"))
            .RespondText("never reached"));

        var failure = await Should.ThrowAsync<TranscriptionException>(
            () => harness.RunAsync(Options with { RequestTimeout = TimeSpan.FromSeconds(5) }));

        failure.Failure.ShouldBe(TranscriptionFailure.RateLimited, "the real reason, so the fallback can answer");
        harness.Waits.ShouldBeEmpty();
        harness.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public void Jittered_backoff_stays_within_half_and_one_and_a_half_steps_and_under_the_cap()
    {
        var client = new AzureTranscriptionClient(new HttpClient(new StubHttpMessageHandler()), random: new Random(42));

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var step = AzureTranscriptionClient.BaseRetryWait * Math.Pow(2, attempt - 1);
            var wait = client.Backoff(attempt, serverWait: null);
            wait.ShouldBeInRange(step * 0.5, step * 1.5);
        }

        client.Backoff(10, serverWait: null).ShouldBe(AzureTranscriptionClient.MaxRetryWait);
        client.Backoff(1, TimeSpan.FromSeconds(-2)).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task A_network_failure_is_retried_and_then_reported_as_network()
    {
        var harness = new Harness(new StubHttpMessageHandler().Throw(new HttpRequestException("DNS failure")));

        var failure = await Should.ThrowAsync<TranscriptionException>(() => harness.RunAsync());

        failure.Failure.ShouldBe(TranscriptionFailure.Network);
        failure.IsTransient.ShouldBeTrue();
        harness.Handler.Requests.Count.ShouldBe(AzureTranscriptionClient.DefaultMaxAttempts);
    }

    [Fact]
    public async Task A_bad_request_surfaces_the_services_own_message()
    {
        const string body = """{"error":{"code":"invalid_value","message":"Invalid file format."}}""";
        var harness = new Harness(new StubHttpMessageHandler().Respond(HttpStatusCode.BadRequest, body));

        (await Should.ThrowAsync<TranscriptionException>(() => harness.RunAsync()))
            .Message.ShouldContain("Invalid file format.");
    }

    [Fact]
    public async Task A_not_found_says_to_check_the_deployment_and_the_api_version()
    {
        var harness = new Harness(new StubHttpMessageHandler().Respond(HttpStatusCode.NotFound));

        var message = (await Should.ThrowAsync<TranscriptionException>(() => harness.RunAsync())).Message;

        message.ShouldContain("deployment name");
        message.ShouldContain("api-version");
    }

    // ---- Time ----

    [Fact]
    public async Task Running_out_of_budget_is_a_transient_timeout()
    {
        var handler = new StubHttpMessageHandler().Hang();
        var harness = new Harness(handler);
        var sent = new TaskCompletionSource();
        handler.Received += (_, _) => sent.TrySetResult();

        var running = harness.RunAsync(cancellationToken: TestContext.Current.CancellationToken);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        harness.Time.Advance(Options.RequestTimeout);

        var failure = await Should.ThrowAsync<TranscriptionException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        failure.Failure.ShouldBe(TranscriptionFailure.Timeout);
        failure.IsTransient.ShouldBeTrue("fallback mode hands a timeout to Parakeet");
    }

    [Fact]
    public async Task The_callers_cancellation_is_a_cancellation_not_a_failure()
    {
        var handler = new StubHttpMessageHandler().Hang();
        var harness = new Harness(handler);
        var sent = new TaskCompletionSource();
        handler.Received += (_, _) => sent.TrySetResult();
        using var cancel = new CancellationTokenSource();

        var running = harness.RunAsync(cancellationToken: cancel.Token);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        await cancel.CancelAsync();

        var thrown = await Should.ThrowAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        thrown.ShouldNotBeOfType<TranscriptionException>();
        handler.Requests.Count.ShouldBe(1, "a cancelled dictation is never retried");
    }

    [Fact]
    public async Task An_already_cancelled_call_sends_nothing()
    {
        var harness = new Harness(new StubHttpMessageHandler().RespondText("never"));

        await Should.ThrowAsync<OperationCanceledException>(
            () => harness.RunAsync(cancellationToken: new CancellationToken(canceled: true)));
        harness.Handler.Requests.ShouldBeEmpty();
    }

    // ---- The key ----

    [Fact]
    public async Task The_key_never_appears_in_a_failure_even_when_the_service_echoes_it()
    {
        var echoed = "{\"error\":{\"message\":\"Key " + Key + " is not valid for this resource.\"}}";
        var statuses = new[]
        {
            HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden,
            HttpStatusCode.NotFound, HttpStatusCode.Conflict, HttpStatusCode.TooManyRequests,
        };

        foreach (var status in statuses)
        {
            var harness = new Harness(new StubHttpMessageHandler().Respond(status, echoed));
            var failure = await Should.ThrowAsync<TranscriptionException>(() => harness.RunAsync());

            failure.Message.ShouldNotContain(Key);
            failure.ToString().ShouldNotContain(Key);
        }

        var network = new Harness(new StubHttpMessageHandler().Throw(new HttpRequestException("refused")));
        (await Should.ThrowAsync<TranscriptionException>(() => network.RunAsync())).ToString().ShouldNotContain(Key);
    }

    [Fact]
    public void The_options_hold_no_key_to_print()
    {
        Options.ToString().ShouldNotContain("key", Case.Insensitive);
    }

    [Fact]
    public async Task A_missing_key_is_not_configured_and_sends_nothing()
    {
        var handler = new StubHttpMessageHandler().RespondText("never");
        var client = new AzureTranscriptionClient(new HttpClient(handler));

        (await Should.ThrowAsync<TranscriptionException>(
                () => client.TranscribeAsync(Options, " ", new float[16], [])))
            .Failure.ShouldBe(TranscriptionFailure.NotConfigured);
        handler.Requests.ShouldBeEmpty();
    }

    // ---- The transcriber over the client ----

    [Fact]
    public async Task The_transcriber_reads_its_options_and_key_on_every_call()
    {
        var handler = new StubHttpMessageHandler().RespondText("one").RespondText("two");
        var options = Options;
        var key = "first-key";
        await using var transcriber = new AzureOpenAITranscriber(
            new AzureTranscriptionClient(new HttpClient(handler)), () => options, () => key);

        transcriber.IsReady.ShouldBeTrue();
        (await transcriber.TranscribeAsync(new float[160], [], CancellationToken.None)).ShouldBe("one");

        options = Options with { Deployment = "fast" };
        key = "second-key";
        (await transcriber.TranscribeAsync(new float[160], [], CancellationToken.None)).ShouldBe("two");

        handler.Requests[1].Headers["api-key"].ShouldBe("second-key");
        handler.Requests[1].Part("model")!.Text.ShouldBe("fast");
    }

    // ---- The connection test ----

    [Fact]
    public async Task The_connection_test_sends_one_second_of_quiet_tone_once_and_reports_the_region()
    {
        var handler = new StubHttpMessageHandler().RespondText("", ("x-ms-region", "East US 2"));
        var tester = new ConnectionTester(new AzureTranscriptionClient(new HttpClient(handler)));

        var result = await tester.TestAsync(Options, Key, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        result.StatusCode.ShouldBe(200);
        result.Region.ShouldBe("East US 2");
        result.Message.ShouldContain("East US 2");

        var wav = handler.Requests.ShouldHaveSingleItem().Part("file")!.Body;
        wav.Length.ShouldBe(44 + 16000 * 2);
        var peak = Enumerable.Range(0, 16000)
            .Max(i => Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2))));
        peak.ShouldBeInRange(3200, 3300, "a tenth of full scale");
    }

    [Fact]
    public async Task The_connection_test_does_not_retry_and_explains_the_failure()
    {
        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.ServiceUnavailable);
        var tester = new ConnectionTester(new AzureTranscriptionClient(new HttpClient(handler)));

        var result = await tester.TestAsync(Options, Key, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        result.StatusCode.ShouldBe(503);
        result.Failure.ShouldBe(TranscriptionFailure.ServerError);
        result.Message.ShouldContain("gpt-4o-transcribe");
        handler.Requests.Count.ShouldBe(1);
    }
}
