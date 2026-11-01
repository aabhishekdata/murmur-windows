using Murmur.Abstractions;

namespace Murmur.Cloud;

/// <summary>
/// Transcribes with Azure OpenAI (<c>gpt-4o-transcribe</c> or <c>gpt-4o-mini-transcribe</c>),
/// using the user's own resource and key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audio leaves the machine here, and only here.</b> This is constructed only when the user
/// has chosen a cloud engine and filled in their own endpoint, deployment and key; Parakeet on
/// the machine stays the default and the fallback.
/// </para>
/// <para>
/// The options and the key are read on every call rather than captured, so switching between
/// the Accurate and Fast deployments, or replacing the key, applies to the next dictation with
/// no restart and nothing to rebuild.
/// </para>
/// <para>
/// There is nothing to load: <see cref="LoadAsync"/> only reports whether the engine is set up,
/// and makes no network call, so starting the app in a cloud mode costs nothing.
/// </para>
/// </remarks>
public sealed class AzureOpenAITranscriber : ITranscriber, ITranscriberDescriptor
{
    private readonly AzureTranscriptionClient _client;
    private readonly Func<AzureTranscriptionOptions?> _options;
    private readonly Func<string?> _apiKey;

    /// <summary>Creates the engine.</summary>
    /// <param name="client">Sends the requests.</param>
    /// <param name="options">The active deployment, or null when the engine is not set up. Read per call.</param>
    /// <param name="apiKey">The key, or null when none is saved. Read per call.</param>
    public AzureOpenAITranscriber(
        AzureTranscriptionClient client,
        Func<AzureTranscriptionOptions?> options,
        Func<string?> apiKey)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(apiKey);

        _client = client;
        _options = options;
        _apiKey = apiKey;
    }

    /// <inheritdoc />
    public TranscriberKind Kind => TranscriberKind.Azure;

    /// <inheritdoc />
    /// <remarks>Set up, not reachable: whether the network is up is asked per dictation.</remarks>
    public bool IsReady => _options() is not null && !string.IsNullOrWhiteSpace(_apiKey());

    /// <inheritdoc />
    public ValueTask<bool> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(IsReady);

    /// <inheritdoc />
    public async ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken)
    {
        // Neither is transient, so fallback mode reports them rather than quietly answering
        // with Parakeet: a user who chose the cloud and is being heard locally ought to know.
        var options = _options() ?? throw new TranscriptionException(
            TranscriptionFailure.NotConfigured,
            "Azure transcription is not set up. Add the endpoint and a deployment in Settings.");

        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new TranscriptionException(
                TranscriptionFailure.NotConfigured, "No Azure API key is saved. Add it in Settings.");
        }

        var result = await _client
            .TranscribeAsync(options, key, samples, biasPhrases, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.Text;
    }

    /// <inheritdoc />
    /// <remarks>The shared <see cref="HttpClient"/> belongs to whoever created it.</remarks>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
