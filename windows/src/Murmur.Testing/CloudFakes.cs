using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Murmur.Abstractions;

namespace Murmur.Testing;

/// <summary>
/// Stands in for DPAPI: reversible, recognisable, and able to fail on demand.
/// </summary>
/// <remarks>
/// Not encryption — a marker and a byte flip — but it has the two properties a test needs: what
/// lands on disk is not the plaintext, and a blob it did not write refuses to decrypt, just as
/// DPAPI refuses one written by another user.
/// </remarks>
public sealed class FakeDataProtector : IDataProtector
{
    private static readonly byte[] Marker = "FAKEDPAPI:"u8.ToArray();

    /// <summary>When true, every <see cref="Unprotect"/> fails, as on a restored profile.</summary>
    public bool FailUnprotect { get; set; }

    /// <summary>How many times <see cref="Unprotect"/> was called.</summary>
    public int UnprotectCalls { get; private set; }

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var blob = new byte[Marker.Length + plaintext.Length];
        Marker.CopyTo(blob, 0);
        for (var i = 0; i < plaintext.Length; i++) blob[Marker.Length + i] = (byte)(plaintext[i] ^ 0x5A);
        return blob;
    }

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        UnprotectCalls++;
        if (FailUnprotect || !protectedData.StartsWith(Marker))
        {
            throw new CryptographicException("The data is invalid.");
        }

        var body = protectedData[Marker.Length..];
        var plain = new byte[body.Length];
        for (var i = 0; i < body.Length; i++) plain[i] = (byte)(body[i] ^ 0x5A);
        return plain;
    }
}

/// <summary>A secret store that keeps everything in a dictionary.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string?> _secrets = new(StringComparer.Ordinal);

    /// <summary>How many times the store was looked at, by <see cref="Read"/> or <see cref="Contains"/>.</summary>
    public int Lookups { get; private set; }

    /// <summary>When set, <see cref="Remove"/> throws this, as a locked file would.</summary>
    public Exception? RemoveFailure { get; set; }

    /// <summary>Marks <paramref name="name"/> as present but undecryptable.</summary>
    /// <param name="name">Which secret.</param>
    public void Corrupt(string name) => _secrets[name] = null;

    /// <inheritdoc />
    public string? Read(string name)
    {
        Lookups++;
        return _secrets.GetValueOrDefault(name);
    }

    /// <inheritdoc />
    public void Write(string name, string value) => _secrets[name] = value;

    /// <inheritdoc />
    public void Remove(string name)
    {
        if (RemoveFailure is { } failure) throw failure;
        _secrets.Remove(name);
    }

    /// <inheritdoc />
    public bool Contains(string name)
    {
        Lookups++;
        return _secrets.ContainsKey(name);
    }
}

/// <summary>One part of a multipart request, as the stub handler saw it.</summary>
/// <param name="Name">The form field name.</param>
/// <param name="FileName">The file name, for a file part.</param>
/// <param name="ContentType">The part's media type, if it had one.</param>
/// <param name="Body">The part's bytes.</param>
public sealed record RecordedPart(string Name, string? FileName, string? ContentType, byte[] Body)
{
    /// <summary>The body as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>A request the stub handler received, captured before it could be disposed.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">The full request URI.</param>
/// <param name="Headers">Every request header, joined with commas.</param>
/// <param name="Parts">The multipart form parts, in order; empty for any other body.</param>
public sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<RecordedPart> Parts)
{
    /// <summary>The part called <paramref name="name"/>, or null.</summary>
    public RecordedPart? Part(string name) => Parts.FirstOrDefault(p => p.Name == name);
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a script and makes no network call.
/// </summary>
/// <remarks>
/// Each request takes the next scripted answer; the last one repeats once the script runs out.
/// With no script at all every request fails as if the network were down, so a test that forgot
/// to script an answer can never reach a real server.
/// </remarks>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();
    private readonly Lock _lock = new();
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _last;

    /// <summary>Every request received, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Answers the next request with <paramref name="status"/> and <paramref name="body"/>.</summary>
    /// <param name="status">The status code.</param>
    /// <param name="body">A JSON body, or null for none.</param>
    /// <param name="headers">Response headers to add.</param>
    /// <returns>This handler, to chain.</returns>
    public StubHttpMessageHandler Respond(
        HttpStatusCode status,
        string? body = null,
        params (string Name, string Value)[] headers) => Then((_, _) =>
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json"),
        };

        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return Task.FromResult(response);
    });

    /// <summary>Answers the next request with a successful transcription of <paramref name="text"/>.</summary>
    /// <param name="text">What the service "heard".</param>
    /// <param name="headers">Response headers to add.</param>
    /// <returns>This handler, to chain.</returns>
    public StubHttpMessageHandler RespondText(string text, params (string Name, string Value)[] headers) =>
        Respond(HttpStatusCode.OK, $"{{\"text\":\"{System.Text.Json.JsonEncodedText.Encode(text)}\"}}", headers);

    /// <summary>Fails the next request with <paramref name="exception"/>.</summary>
    /// <param name="exception">What sending throws.</param>
    /// <returns>This handler, to chain.</returns>
    public StubHttpMessageHandler Throw(Exception exception) =>
        Then((_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Never answers the next request; it ends only when cancelled.</summary>
    /// <returns>This handler, to chain.</returns>
    public StubHttpMessageHandler Hang() => Then(async (_, token) =>
    {
        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    });

    /// <summary>Adds any answer to the script.</summary>
    /// <param name="step">Builds the response.</param>
    /// <returns>This handler, to chain.</returns>
    public StubHttpMessageHandler Then(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step)
    {
        lock (_lock) _script.Enqueue(step);
        return this;
    }

    /// <summary>Raised as each request arrives, before it is answered.</summary>
    public event EventHandler<RecordedRequest>? Received;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var recorded = await RecordAsync(request, cancellationToken).ConfigureAwait(false);

        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? step;
        lock (_lock)
        {
            Requests.Add(recorded);
            if (_script.Count > 0) _last = _script.Dequeue();
            step = _last;
        }

        Received?.Invoke(this, recorded);

        if (step is null) throw new HttpRequestException("No scripted response: the stub never reaches a real network.");
        return await step(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RecordedRequest> RecordAsync(HttpRequestMessage request, CancellationToken token)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers) headers[header.Key] = string.Join(",", header.Value);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers) headers[header.Key] = string.Join(",", header.Value);
        }

        var parts = new List<RecordedPart>();
        if (request.Content is MultipartFormDataContent form)
        {
            foreach (var part in form)
            {
                var disposition = part.Headers.ContentDisposition ?? new ContentDispositionHeaderValue("form-data");
                parts.Add(new RecordedPart(
                    Unquote(disposition.Name) ?? string.Empty,
                    Unquote(disposition.FileName),
                    part.Headers.ContentType?.MediaType,
                    await part.ReadAsByteArrayAsync(token).ConfigureAwait(false)));
            }
        }

        return new RecordedRequest(request.Method, request.RequestUri, headers, parts);
    }

    private static string? Unquote(string? value) => value?.Trim('"');
}
