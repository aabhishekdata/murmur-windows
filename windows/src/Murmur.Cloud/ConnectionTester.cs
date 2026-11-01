using System.Globalization;
using Murmur.Abstractions;

namespace Murmur.Cloud;

/// <summary>What one connection test found.</summary>
/// <param name="Deployment">The deployment that was tried.</param>
/// <param name="Succeeded">Whether the service transcribed the test tone.</param>
/// <param name="StatusCode">The HTTP status, when there was a response.</param>
/// <param name="Latency">From sending to the answer.</param>
/// <param name="Region">The region that answered, from <c>x-ms-region</c>, when sent.</param>
/// <param name="Failure">Why it failed, or null when it did not.</param>
/// <param name="Message">One line for the Settings screen, with a hint at the fix on failure.</param>
public sealed record ConnectionTestResult(
    string Deployment,
    bool Succeeded,
    int? StatusCode,
    TimeSpan Latency,
    string? Region,
    TranscriptionFailure? Failure,
    string Message);

/// <summary>The Settings screen's "Test connection": one real request, with a second of tone.</summary>
/// <remarks>
/// <para>
/// A tone rather than silence, and quiet rather than loud: the service answers silence with an
/// empty transcript just as happily, but a real signal also proves the upload and the WAV
/// encoding are accepted. What it hears in a tone does not matter; that it answers does.
/// </para>
/// <para>
/// One attempt and no retries. A test exists to say what is wrong now, and retrying a 429
/// would only make the answer slower without making it truer.
/// </para>
/// </remarks>
public sealed class ConnectionTester
{
    private readonly AzureTranscriptionClient _client;
    private readonly TimeProvider _time;

    /// <summary>Creates a tester over the app's client.</summary>
    /// <param name="client">Sends the request.</param>
    /// <param name="time">Times it; replaced in tests.</param>
    public ConnectionTester(AzureTranscriptionClient client, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>One second of a 440 Hz tone at a tenth of full scale.</summary>
    /// <returns>16 kHz mono float samples.</returns>
    public static float[] Tone()
    {
        var samples = new float[WavEncoder.SampleRate];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.1f * MathF.Sin(2 * MathF.PI * 440 * i / WavEncoder.SampleRate);
        }

        return samples;
    }

    /// <summary>Sends the tone to <paramref name="options"/>' deployment.</summary>
    /// <param name="options">The deployment to test.</param>
    /// <param name="apiKey">The key.</param>
    /// <param name="cancellationToken">Abandons the test.</param>
    /// <returns>What happened. Never throws for a failed request; that is a result.</returns>
    public async Task<ConnectionTestResult> TestAsync(
        AzureTranscriptionOptions options,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var started = _time.GetTimestamp();
        try
        {
            var result = await _client
                .TranscribeAsync(options, apiKey, Tone(), [], maxAttempts: 1, cancellationToken)
                .ConfigureAwait(false);

            var latency = _time.GetElapsedTime(started);
            var where = result.Region is null ? string.Empty : $" from {result.Region}";
            return new ConnectionTestResult(
                options.Deployment,
                Succeeded: true,
                result.StatusCode,
                latency,
                result.Region,
                Failure: null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Connected to {options.Deployment}: answered in {latency.TotalMilliseconds:0} ms{where}."));
        }
        catch (TranscriptionException ex)
        {
            return new ConnectionTestResult(
                options.Deployment,
                Succeeded: false,
                ex.StatusCode,
                _time.GetElapsedTime(started),
                Region: null,
                ex.Failure,
                $"{options.Deployment}: {ex.Message}");
        }
    }
}
