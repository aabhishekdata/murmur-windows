using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Murmur.Speech;
using Xunit;

namespace Murmur.E2E.Tests;

/// <summary>One request the stand-in endpoint received, and what it made of it.</summary>
internal sealed record MockRequest
{
    /// <summary>When it arrived.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The deployment named in the path.</summary>
    public required string Deployment { get; init; }

    /// <summary>The <c>api-version</c> query parameter, or null.</summary>
    public string? ApiVersion { get; init; }

    /// <summary>The <c>api-key</c> header, or null.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The <c>model</c> field.</summary>
    public string? Model { get; init; }

    /// <summary>The <c>response_format</c> field.</summary>
    public string? ResponseFormat { get; init; }

    /// <summary>The <c>chunking_strategy</c> field.</summary>
    public string? ChunkingStrategy { get; init; }

    /// <summary>The <c>language</c> field, or null when it was not sent.</summary>
    public string? Language { get; init; }

    /// <summary>The <c>prompt</c> field, or null when it was not sent.</summary>
    public string? Prompt { get; init; }

    /// <summary>The <c>file</c> part's file name.</summary>
    public string? FileName { get; init; }

    /// <summary>The <c>file</c> part's declared format.</summary>
    public Wav.Header? Audio { get; init; }

    /// <summary>Every form field's name, in order.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>What was wrong with the request; empty when it was what Azure expects.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>The status it was answered with.</summary>
    public int Status { get; set; }

    /// <summary>The text it was answered with, when it was transcribed.</summary>
    public string? Text { get; set; }

    /// <summary>How long Parakeet took to transcribe it, server side.</summary>
    public TimeSpan Transcription { get; set; }
}

/// <summary>A scripted answer that replaces the next request's normal one.</summary>
/// <param name="Status">The HTTP status; 0 for <see cref="Hang"/>.</param>
/// <param name="Message">The <c>error.message</c> to send, or null.</param>
/// <param name="RetryAfterMs">A <c>retry-after-ms</c> header to send, or null.</param>
internal sealed record MockFailure(int Status, string? Message = null, int? RetryAfterMs = null)
{
    /// <summary>Never answers; the request ends only when the client gives up.</summary>
    public static MockFailure Hang { get; } = new(0);
}

/// <summary>
/// A stand-in for Azure OpenAI's transcription endpoint on <c>http://127.0.0.1:&lt;random port&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// It checks each request the way the service does — the <c>api-key</c> header, the
/// <c>api-version</c>, the deployment in the path, and every multipart field, including that the
/// file really is PCM16 16 kHz mono WAV — records it, and answers with a <b>real</b>
/// transcription of the audio it was sent, made by Parakeet. So the cloud path is exercised with
/// genuine words end to end, and only the model behind the URL is a stand-in.
/// </para>
/// <para>
/// Failures are scripted with <see cref="Enqueue"/>; each replaces one request's answer, after the
/// request has been recorded. Requests are answered one at a time, as one dictation is in flight
/// at a time in the app.
/// </para>
/// </remarks>
internal sealed class MockAzureServer : IAsyncDisposable
{
    /// <summary>The api-versions the stand-in accepts, as Azure does for this operation.</summary>
    public static readonly IReadOnlySet<string> ApiVersions = new HashSet<string>(StringComparer.Ordinal)
    {
        "2025-03-01-preview",
        "2025-04-01-preview",
    };

    private static readonly string[] KnownFields = ["file", "model", "response_format", "chunking_strategy", "language", "prompt"];

    private readonly WebApplication _app;
    private readonly string _key;
    private readonly IReadOnlySet<string> _deployments;
    private readonly Queue<MockFailure> _script = new();
    private readonly List<MockRequest> _requests = [];
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _one = new(1, 1);
    private bool _stopped;
    private int _connections;

    private MockAzureServer(WebApplication app, string key, IReadOnlySet<string> deployments)
    {
        _app = app;
        _key = key;
        _deployments = deployments;
    }

    /// <summary>The base URL, such as <c>http://127.0.0.1:51234</c>.</summary>
    public Uri Endpoint { get; private set; } = null!;

    /// <summary>
    /// How many TCP connections were opened to it, including ones that never became a request,
    /// such as a TLS handshake from a client told the endpoint is https.
    /// </summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Every request received, in order.</summary>
    public IReadOnlyList<MockRequest> Requests
    {
        get
        {
            lock (_lock) return [.. _requests];
        }
    }

    /// <summary>Starts a server.</summary>
    /// <param name="key">The only api-key it accepts.</param>
    /// <param name="deployments">The deployments that exist.</param>
    /// <returns>The running server.</returns>
    public static async Task<MockAzureServer> StartAsync(string key, params string[] deployments)
    {
        MockAzureServer? server = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.Use(next => connection =>
            {
                if (server is not null) Interlocked.Increment(ref server._connections);
                return next(connection);
            }));
            options.Limits.MaxRequestBodySize = 30L * 1024 * 1024;
        });

        var app = builder.Build();
        server = new MockAzureServer(app, key, new HashSet<string>(deployments, StringComparer.Ordinal));
        app.MapPost("/openai/deployments/{deployment}/audio/transcriptions", (RequestDelegate)server.HandleAsync);
        app.MapFallback((RequestDelegate)(context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }));

        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        server.Endpoint = new Uri(address);
        return server;
    }

    /// <summary>Replaces the next request's answer with <paramref name="failure"/>.</summary>
    /// <param name="failure">The scripted answer.</param>
    /// <param name="times">How many requests in a row it answers.</param>
    /// <returns>This server, to chain.</returns>
    public MockAzureServer Enqueue(MockFailure failure, int times = 1)
    {
        lock (_lock)
        {
            for (var i = 0; i < times; i++) _script.Enqueue(failure);
        }

        return this;
    }

    /// <summary>Stops listening, so a connection is refused as if the network were down.</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _one.Dispose();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var aborted = context.RequestAborted;
        var request = await ReadAsync(context, aborted).ConfigureAwait(false);

        MockFailure? scripted;
        lock (_lock)
        {
            _requests.Add(request.Record);
            scripted = _script.Count > 0 ? _script.Dequeue() : null;
        }

        if (scripted is not null)
        {
            await FailAsync(context, request.Record, scripted, aborted).ConfigureAwait(false);
            return;
        }

        // The checks, in the order Azure makes them: who you are, then where, then what.
        if (!string.Equals(request.Record.ApiKey, _key, StringComparison.Ordinal))
        {
            await ErrorAsync(context, request.Record, 401, "Access denied due to invalid subscription key or wrong API endpoint.", aborted)
                .ConfigureAwait(false);
            return;
        }

        if (request.Record.ApiVersion is null || !ApiVersions.Contains(request.Record.ApiVersion))
        {
            await ErrorAsync(context, request.Record, 404, "Resource not found", aborted).ConfigureAwait(false);
            return;
        }

        if (!_deployments.Contains(request.Record.Deployment))
        {
            await ErrorAsync(
                    context,
                    request.Record,
                    404,
                    "The API deployment for this resource does not exist.",
                    aborted)
                .ConfigureAwait(false);
            return;
        }

        if (request.Record.Problems.Count > 0 || request.Samples is null)
        {
            await ErrorAsync(context, request.Record, 400, string.Join(" ", request.Record.Problems), aborted)
                .ConfigureAwait(false);
            return;
        }

        // A real transcription of the audio that arrived, so the text the app receives over HTTP
        // is what was actually said in the clip.
        await _one.WaitAsync(aborted).ConfigureAwait(false);
        try
        {
            var watch = Stopwatch.StartNew();
            var text = await SharedParakeet.TranscribeAsync(request.Samples, aborted).ConfigureAwait(false);
            request.Record.Transcription = watch.Elapsed;
            request.Record.Text = text;
            request.Record.Status = 200;

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.Headers["x-ms-region"] = "Local Test";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { text }), aborted).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    private sealed record Received(MockRequest Record, float[]? Samples);

    private static async Task<Received> ReadAsync(HttpContext context, CancellationToken token)
    {
        var problems = new List<string>();
        var http = context.Request;
        var deployment = http.RouteValues["deployment"] as string ?? string.Empty;

        if (!http.Query.TryGetValue("api-version", out var apiVersion) || apiVersion.Count != 1)
        {
            problems.Add("api-version is missing.");
        }

        string? Header(string name) => http.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

        if (!http.HasFormContentType)
        {
            problems.Add("The body is not multipart/form-data.");
            return new Received(
                new MockRequest
                {
                    At = DateTimeOffset.Now,
                    Deployment = deployment,
                    ApiVersion = apiVersion.FirstOrDefault(),
                    ApiKey = Header("api-key"),
                    Problems = problems,
                },
                null);
        }

        var form = await http.ReadFormAsync(token).ConfigureAwait(false);
        string? Field(string name) => form.TryGetValue(name, out var value) ? value.ToString() : null;

        var fields = form.Keys.Concat(form.Files.Select(f => f.Name)).ToList();
        problems.AddRange(fields.Where(f => !KnownFields.Contains(f)).Select(f => $"Unknown field {f}."));

        var model = Field("model");
        if (string.IsNullOrWhiteSpace(model)) problems.Add("model is required.");
        if (Field("response_format") != "json") problems.Add("response_format must be json.");
        if (Field("chunking_strategy") != "auto") problems.Add("chunking_strategy must be auto.");

        var file = form.Files.GetFile("file");
        Wav.Header? header = null;
        float[]? samples = null;
        if (file is null)
        {
            problems.Add("file is required.");
        }
        else
        {
            using var stream = new MemoryStream();
            await file.CopyToAsync(stream, token).ConfigureAwait(false);
            try
            {
                header = Wav.Parse(stream.GetBuffer().AsSpan(0, (int)stream.Length), out _);
                if (header.Value.IsPcm16Mono16k) samples = Wav.Read(stream.GetBuffer().AsSpan(0, (int)stream.Length));
                else problems.Add($"The file must be PCM16 16 kHz mono WAV, not {header}.");
            }
            catch (InvalidDataException ex)
            {
                problems.Add($"The file is not a WAV file: {ex.Message}");
            }
        }

        return new Received(
            new MockRequest
            {
                At = DateTimeOffset.Now,
                Deployment = deployment,
                ApiVersion = apiVersion.FirstOrDefault(),
                ApiKey = Header("api-key"),
                Model = model,
                ResponseFormat = Field("response_format"),
                ChunkingStrategy = Field("chunking_strategy"),
                Language = Field("language"),
                Prompt = Field("prompt"),
                FileName = file?.FileName,
                Audio = header,
                Fields = fields,
                Problems = problems,
            },
            samples);
    }

    private static async Task FailAsync(HttpContext context, MockRequest record, MockFailure failure, CancellationToken token)
    {
        if (failure.Status == 0)
        {
            // Hold the request until the client gives up on it. RequestAborted fires when it does.
            try
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                record.Status = 0;
            }

            return;
        }

        if (failure.RetryAfterMs is { } wait) context.Response.Headers["retry-after-ms"] = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ErrorAsync(context, record, failure.Status, failure.Message ?? $"Scripted {failure.Status}.", token).ConfigureAwait(false);
    }

    private static async Task ErrorAsync(HttpContext context, MockRequest record, int status, string message, CancellationToken token)
    {
        record.Status = status;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var body = JsonSerializer.Serialize(new { error = new { code = status.ToString(System.Globalization.CultureInfo.InvariantCulture), message } });
        await context.Response.WriteAsync(body, token).ConfigureAwait(false);
    }
}

/// <summary>
/// The Parakeet model the stand-in endpoint transcribes with: one instance, loaded on first use,
/// kept for the whole run.
/// </summary>
/// <remarks>
/// Separate from the one inside each <see cref="Pipeline"/>, which is built, loaded and unloaded the
/// way the app does it. This one is the "service", and only answers HTTP requests.
/// </remarks>
internal static class SharedParakeet
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static ParakeetTranscriber? _model;

    /// <summary>Transcribes <paramref name="samples"/>.</summary>
    /// <param name="samples">16 kHz mono float.</param>
    /// <param name="token">Cancels the wait for the model.</param>
    /// <returns>What Parakeet heard.</returns>
    public static async Task<string> TranscribeAsync(float[] samples, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_model is null)
            {
                var model = new ParakeetTranscriber(E2E.ModelDirectory);
                if (!await model.LoadAsync(token).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The stand-in endpoint could not load Parakeet.");
                }

                _model = model;
            }

            return await _model.TranscribeAsync(samples, [], token).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
