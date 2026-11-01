using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Murmur.Abstractions;

namespace Murmur.Cloud;

/// <summary>A transcription the service answered.</summary>
/// <param name="Text">What was heard; empty when the service heard nothing.</param>
/// <param name="StatusCode">The HTTP status of the answer.</param>
/// <param name="Region">The Azure region that served it, from <c>x-ms-region</c>, when sent.</param>
/// <param name="Attempts">How many requests it took.</param>
/// <param name="Elapsed">From the first request to the answer, waits included.</param>
public sealed record AzureTranscription(string Text, int StatusCode, string? Region, int Attempts, TimeSpan Elapsed);

/// <summary>
/// Sends audio to an Azure OpenAI transcription deployment, retrying what is worth retrying.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is retried:</b> 408, 429, 500, 502, 503 and 504, and failures to reach the service at
/// all. Nothing else: a 400, 401, 403 or 404 will say the same thing however often it is asked.
/// The service's own <c>retry-after-ms</c>, then <c>Retry-After</c> (seconds or an HTTP date),
/// is honoured; otherwise the wait backs off exponentially with jitter. Every wait is capped at
/// <see cref="MaxRetryWait"/>, and none is started that would end after the request's budget.
/// </para>
/// <para>
/// <b>The budget</b> is <see cref="AzureTranscriptionOptions.RequestTimeout"/>, covering every
/// attempt and every wait. Running out is a <see cref="TranscriptionFailure.Timeout"/>, which is
/// transient, so the offline fallback can still answer. The caller's own cancellation is
/// different: it surfaces as <see cref="OperationCanceledException"/>, never as a failure,
/// because nobody wants anything further from a dictation the user abandoned.
/// </para>
/// <para>
/// <b>The key</b> goes in the <c>api-key</c> header and nowhere else — not the URL, not an
/// exception message, not a log. Any text echoed back by the service is scrubbed of it before
/// it is put in a message, in case a gateway reflects request headers into its error body.
/// </para>
/// </remarks>
public sealed class AzureTranscriptionClient
{
    /// <summary>The largest upload the service accepts.</summary>
    public const long MaxUploadBytes = 25L * 1024 * 1024;

    /// <summary>Requests made per transcription when nothing says otherwise.</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>The longest single wait between attempts.</summary>
    public static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(8);

    /// <summary>The first backoff, doubled for each attempt after.</summary>
    public static readonly TimeSpan BaseRetryWait = TimeSpan.FromMilliseconds(500);

    private const int MaxServiceMessageLength = 300;

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates a client over a shared <see cref="HttpClient"/>.</summary>
    /// <param name="http">
    /// Shared for the life of the process, with an infinite timeout: this class enforces its
    /// own budget, and a second, different timeout underneath would make the two fight.
    /// </param>
    /// <param name="time">Drives the budget and the waits; replaced in tests.</param>
    /// <param name="random">The jitter source; seeded in tests so the waits are predictable.</param>
    /// <param name="delay">How a wait is performed; replaced in tests so none is real.</param>
    public AzureTranscriptionClient(
        HttpClient http,
        TimeProvider? time = null,
        Random? random = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _time = time ?? TimeProvider.System;
        _random = random ?? Random.Shared;
        _delay = delay ?? ((wait, token) => Task.Delay(wait, _time, token));
    }

    /// <summary>Transcribes one utterance.</summary>
    /// <param name="options">The deployment and its settings.</param>
    /// <param name="apiKey">The key, sent only in the <c>api-key</c> header.</param>
    /// <param name="samples">16 kHz mono float.</param>
    /// <param name="biasPhrases">Dictionary terms for the prompt.</param>
    /// <param name="maxAttempts">How many requests to make at most; 1 disables retrying.</param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    /// <returns>What the service heard.</returns>
    /// <exception cref="TranscriptionException">The service refused, failed, or ran out of time.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<AzureTranscription> TranscribeAsync(
        AzureTranscriptionOptions options,
        string apiKey,
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranscriptionException(TranscriptionFailure.NotConfigured, "No Azure API key is saved.");
        }

        // Checked before anything is encoded or sent: the service would refuse it after the
        // whole upload, which on a slow link is a long wait to be told no.
        var size = WavEncoder.ByteCount(samples.Length);
        if (size > MaxUploadBytes)
        {
            throw new TranscriptionException(
                TranscriptionFailure.PayloadTooLarge,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The recording is {size / (1024.0 * 1024.0):0.#} MB; Azure accepts at most 25 MB."));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var wav = WavEncoder.EncodePooled(samples.Span);
        var prompt = TranscriptionPrompt.Build(biasPhrases, options.LanguageHint);

        var started = _time.GetTimestamp();
        var budget = options.RequestTimeout > TimeSpan.Zero ? options.RequestTimeout : AzureTranscriptionOptions.DefaultRequestTimeout;
        var deadline = _time.GetUtcNow() + budget;

        using var budgetSource = new CancellationTokenSource(budget, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetSource.Token);
        var token = linked.Token;

        var attempts = Math.Max(1, maxAttempts);
        for (var attempt = 1; ; attempt++)
        {
            TranscriptionException failure;
            TimeSpan? serverWait = null;

            try
            {
                using var request = BuildRequest(options, apiKey, wav.Memory, prompt);
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return new AzureTranscription(
                        ParseText(body),
                        (int)response.StatusCode,
                        Header(response, "x-ms-region"),
                        attempt,
                        _time.GetElapsedTime(started));
                }

                failure = Map(response.StatusCode, ServiceMessage(body), apiKey);
                serverWait = RetryAfter(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (budgetSource.IsCancellationRequested)
            {
                throw TimedOut(budget, ex);
            }
            catch (Exception ex) when (IsNetworkFailure(ex))
            {
                failure = new TranscriptionException(
                    TranscriptionFailure.Network,
                    "Could not reach the Azure endpoint. Check the URL, your network and any proxy.",
                    innerException: ex);
            }

            if (attempt >= attempts || !failure.IsTransient) throw failure;

            var wait = Backoff(attempt, serverWait);

            // A wait that would outlast the budget is not worth starting: the request after it
            // could never finish in time. Failing now leaves the fallback that time instead.
            if (_time.GetUtcNow() + wait >= deadline) throw failure;

            try
            {
                await _delay(wait, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (budgetSource.IsCancellationRequested)
            {
                throw TimedOut(budget, ex);
            }
        }
    }

    /// <summary>How long to wait before attempt <paramref name="failedAttempt"/> + 1.</summary>
    /// <param name="failedAttempt">The attempt that just failed, from 1.</param>
    /// <param name="serverWait">What the service asked for, if anything.</param>
    /// <returns>The wait, never more than <see cref="MaxRetryWait"/>.</returns>
    internal TimeSpan Backoff(int failedAttempt, TimeSpan? serverWait)
    {
        TimeSpan wait;
        if (serverWait is { } asked)
        {
            wait = asked < TimeSpan.Zero ? TimeSpan.Zero : asked;
        }
        else
        {
            // Jittered between half and one-and-a-half times the doubling step, so several
            // clients turned away by the same 503 do not all come back on the same tick.
            var step = BaseRetryWait * Math.Pow(2, Math.Min(failedAttempt - 1, 10));
#pragma warning disable CA5394 // Jitter, not security: predictability is harmless and tests rely on a seed.
            wait = step * (0.5 + _random.NextDouble());
#pragma warning restore CA5394
        }

        return wait > MaxRetryWait ? MaxRetryWait : wait;
    }

    private static HttpRequestMessage BuildRequest(
        AzureTranscriptionOptions options,
        string apiKey,
        ReadOnlyMemory<byte> wav,
        string? prompt)
    {
        // Built afresh per attempt: a request message may be sent only once.
        var request = new HttpRequestMessage(HttpMethod.Post, options.TranscriptionUri);
        request.Headers.TryAddWithoutValidation("api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var file = new ReadOnlyMemoryContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        var form = new MultipartFormDataContent
        {
            { file, "file", "audio.wav" },
            { new StringContent(options.Deployment), "model" },
            { new StringContent("json"), "response_format" },
            { new StringContent("auto"), "chunking_strategy" },
        };

        if (!string.IsNullOrWhiteSpace(options.Language)) form.Add(new StringContent(options.Language.Trim()), "language");
        if (prompt is not null) form.Add(new StringContent(prompt), "prompt");

        request.Content = form;
        return request;
    }

    /// <summary>Turns a failed response into the failure the rest of the app understands.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="serviceMessage">The <c>error.message</c> the service sent, if any.</param>
    /// <param name="apiKey">Scrubbed out of anything that is echoed.</param>
    /// <returns>The exception to throw.</returns>
    internal static TranscriptionException Map(HttpStatusCode status, string? serviceMessage, string apiKey)
    {
        var code = (int)status;
        var detail = Scrub(serviceMessage, apiKey);
        var suffix = detail is null ? string.Empty : $" Azure said: {detail}";

        return status switch
        {
            HttpStatusCode.BadRequest => new TranscriptionException(
                TranscriptionFailure.BadRequest,
                detail is null ? "Azure rejected the request (400)." : $"Azure rejected the request (400): {detail}",
                code),
            HttpStatusCode.Unauthorized => new TranscriptionException(
                TranscriptionFailure.Unauthorized,
                "Azure rejected the API key (401). Copy Key 1 from the resource's Keys and Endpoint page and save it again.",
                code),
            HttpStatusCode.Forbidden => new TranscriptionException(
                TranscriptionFailure.Forbidden,
                "The key is valid but may not use this resource (403). Check the resource's network rules." + suffix,
                code),
            HttpStatusCode.NotFound => new TranscriptionException(
                TranscriptionFailure.NotFound,
                "Not found (404). Check the deployment name and the api-version.",
                code),
            HttpStatusCode.ProxyAuthenticationRequired => new TranscriptionException(
                TranscriptionFailure.ProxyAuthenticationRequired,
                "Proxy authentication required (407). Your network's proxy did not accept this PC's sign-in.",
                code),
            HttpStatusCode.RequestTimeout => new TranscriptionException(
                TranscriptionFailure.Timeout, "Azure timed out waiting for the request (408).", code),
            HttpStatusCode.RequestEntityTooLarge => new TranscriptionException(
                TranscriptionFailure.PayloadTooLarge, "The recording is too large for Azure (413).", code),
            HttpStatusCode.TooManyRequests => new TranscriptionException(
                TranscriptionFailure.RateLimited,
                "Azure is rate limiting this deployment (429). Wait a moment, or raise its quota.",
                code),
            // Any 5xx, not just the four Azure itself sends: gateways and CDNs in front of an
            // enterprise endpoint answer with the rest (520-529 among them).
            _ when code is >= 500 and <= 599 => new TranscriptionException(
                TranscriptionFailure.ServerError,
                string.Create(CultureInfo.InvariantCulture, $"Azure had a problem on its side ({code})."),
                code),

            // Redirects are not followed, so the key is never sent on to another host.
            _ when code is >= 300 and <= 399 => new TranscriptionException(
                TranscriptionFailure.Unknown,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The endpoint redirected ({code}), which is not followed so the key stays put. Enter the final URL."),
                code),
            _ => new TranscriptionException(
                TranscriptionFailure.Unknown,
                string.Create(CultureInfo.InvariantCulture, $"Azure answered {code}.") + suffix,
                code),
        };
    }

    /// <summary>The wait the service asked for, if it said.</summary>
    /// <remarks>
    /// <c>retry-after-ms</c> first — Azure's own, and the precise one — then the standard
    /// header in either of its forms.
    /// </remarks>
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (Header(response, "retry-after-ms") is { } ms &&
            double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) &&
            milliseconds >= 0)
        {
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - _time.GetUtcNow();

        return null;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string ParseText(string body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, CloudJsonContext.Default.TranscriptionResponse)?.Text?.Trim()
                   ?? string.Empty;
        }
        catch (JsonException ex)
        {
            throw new TranscriptionException(
                TranscriptionFailure.Unknown, "Azure answered with something that is not a transcription.", 200, ex);
        }
    }

    private static string? ServiceMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            return JsonSerializer.Deserialize(body, CloudJsonContext.Default.ErrorResponse)?.Error?.Message;
        }
        catch (JsonException)
        {
            // A proxy or gateway's HTML error page. Not worth quoting.
            return null;
        }
    }

    private static string? Scrub(string? text, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var clean = text.Trim();
        if (apiKey.Length > 0) clean = clean.Replace(apiKey, "[key]", StringComparison.Ordinal);
        if (clean.Length > MaxServiceMessageLength) clean = clean[..MaxServiceMessageLength] + "…";
        return clean;
    }

    private static TranscriptionException TimedOut(TimeSpan budget, Exception inner) => new(
        TranscriptionFailure.Timeout,
        string.Create(CultureInfo.InvariantCulture, $"Azure did not answer within {budget.TotalSeconds:0.#} s."),
        innerException: inner);

    /// <summary>Whether <paramref name="ex"/> means the service was never reached or the line dropped.</summary>
    /// <remarks>
    /// A cancellation reaching here is neither the caller's nor the budget's — both are
    /// filtered out before — so it is the handler's own connect timeout, and counts as network.
    /// </remarks>
    private static bool IsNetworkFailure(Exception ex) =>
        ex is HttpRequestException or IOException or SocketException or OperationCanceledException;
}

/// <summary>The body of a successful <c>response_format=json</c> transcription.</summary>
internal sealed record TranscriptionResponse
{
    /// <summary>What was heard.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>The body of a failed request.</summary>
internal sealed record ErrorResponse
{
    /// <summary>The error.</summary>
    [JsonPropertyName("error")]
    public ErrorDetail? Error { get; init; }
}

/// <summary>What went wrong, in the service's words.</summary>
internal sealed record ErrorDetail
{
    /// <summary>A machine-readable code.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    /// <summary>A human-readable message.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>Source-generated JSON for the service's responses.</summary>
[JsonSerializable(typeof(TranscriptionResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class CloudJsonContext : JsonSerializerContext;
