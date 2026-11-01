namespace Murmur.Cloud;

/// <summary>Where, and how, one Azure OpenAI transcription request is sent.</summary>
/// <remarks>
/// <para>
/// Holds no key, on purpose. The key travels separately, straight from the secret store to
/// the request header, so this record can be logged, compared and shown in a test failure
/// without anything sensitive in its <c>ToString</c>.
/// </para>
/// <para>
/// Built afresh for every request from the current settings, so switching between the
/// Accurate and Fast deployments takes effect on the next dictation.
/// </para>
/// </remarks>
public sealed record AzureTranscriptionOptions
{
    /// <summary>The api-version used when the user has not chosen one.</summary>
    /// <remarks>
    /// The first GA-era version that serves <c>gpt-4o-transcribe</c>. If a resource rejects it,
    /// <c>2025-04-01-preview</c> is the one to try; the field is editable for exactly that.
    /// </remarks>
    public const string DefaultApiVersion = "2025-03-01-preview";

    /// <summary>How long one dictation may spend with the service when nothing says otherwise.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The resource, already normalized by <see cref="AzureEndpoint.TryNormalize"/>.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>The deployment name, which Azure also takes as the model.</summary>
    public required string Deployment { get; init; }

    /// <summary>The <c>api-version</c> query parameter.</summary>
    public string ApiVersion { get; init; } = DefaultApiVersion;

    /// <summary>An ISO-639-1 code such as <c>en</c>, or null to let the model detect it.</summary>
    public string? Language { get; init; }

    /// <summary>A sentence about the language or register, added to the prompt; null for none.</summary>
    public string? LanguageHint { get; init; }

    /// <summary>
    /// The whole time one dictation may take: every attempt, and every wait between them.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = DefaultRequestTimeout;

    /// <summary>The transcription URL for this deployment.</summary>
    /// <remarks>
    /// The deployment name is escaped: it is user input, and a slash or a question mark in it
    /// would otherwise address a different resource rather than fail.
    /// </remarks>
    public Uri TranscriptionUri
    {
        get
        {
            var root = Endpoint.AbsoluteUri.TrimEnd('/');
            return new Uri(
                $"{root}/openai/deployments/{Uri.EscapeDataString(Deployment)}/audio/transcriptions"
                + $"?api-version={Uri.EscapeDataString(ApiVersion)}");
        }
    }
}
