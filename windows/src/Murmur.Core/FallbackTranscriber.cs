using System.Net;
using System.Runtime.ExceptionServices;
using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>Raised when a dictation was handed to the fallback engine.</summary>
/// <param name="From">The engine that was asked first.</param>
/// <param name="To">The engine that answered instead.</param>
/// <param name="Cause">
/// What the first engine failed with, or null when it was skipped because it failed recently.
/// </param>
public sealed record TranscriptionFallback(TranscriberKind From, TranscriberKind To, Exception? Cause);

/// <summary>
/// Asks a primary engine, and hands the audio to a fallback when the primary cannot be reached.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which failures fall back is the whole design.</b> Only failures that say nothing about
/// the user's setup do: no network, a timeout, HTTP 408, 429 and 5xx. A rejected key (401),
/// a forbidden resource (403), a deployment that does not exist (404) and a malformed request
/// (400) do not — they surface as errors. Falling back on those would hide a broken setup
/// behind a working one, and the user would go on believing their words were going to the
/// engine they chose. A dictation the user cancelled is never retried on anything.
/// </para>
/// <para>
/// <b>Circuit breaker.</b> After a fallback the primary is not asked again for
/// <see cref="CircuitBreakDuration"/>. With the network down, each dictation would otherwise
/// wait out a full connection timeout before the fallback even started. The operating
/// system saying the network has gone holds the circuit open until it says it is back.
/// </para>
/// <para>
/// <b>Only while the fallback can answer.</b> With no offline model installed, skipping the
/// primary would turn one busy minute in the cloud into a minute of "no model" for every
/// dictation, with the cloud never asked even once it had recovered. So while the fallback is
/// unavailable the circuit is ignored, the primary is always asked, and its own failure is the
/// one reported.
/// </para>
/// </remarks>
public sealed class FallbackTranscriber : ITranscriber, ITranscriberDescriptor
{
    /// <summary>How long the primary is skipped after it fails.</summary>
    public static readonly TimeSpan CircuitBreakDuration = TimeSpan.FromSeconds(60);

    private readonly ITranscriber _primary;
    private readonly ITranscriber _fallback;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan?> _primaryTimeout;
    private readonly Func<bool> _fallbackAvailable;
    private readonly bool _leaveOpen;
    private readonly Lock _lock = new();

    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;
    private bool _networkDown;

    /// <summary>Puts a fallback behind a primary engine.</summary>
    /// <param name="primary">Asked first.</param>
    /// <param name="fallback">Asked when the primary cannot be reached.</param>
    /// <param name="time">Drives the circuit breaker and the primary's timeout.</param>
    /// <param name="primaryTimeout">
    /// How long the primary may take before the fallback is asked instead, read per call;
    /// null for no limit beyond the caller's own.
    /// </param>
    /// <param name="leaveOpen">Whether disposing this leaves both engines alive.</param>
    /// <param name="fallbackAvailable">
    /// Whether the fallback can answer at all right now — for Parakeet, whether its model is
    /// installed. Read per call; null means always.
    /// </param>
    public FallbackTranscriber(
        ITranscriber primary,
        ITranscriber fallback,
        TimeProvider? time = null,
        Func<TimeSpan?>? primaryTimeout = null,
        bool leaveOpen = false,
        Func<bool>? fallbackAvailable = null)
    {
        _primary = primary;
        _fallback = fallback;
        _time = time ?? TimeProvider.System;
        _primaryTimeout = primaryTimeout ?? (static () => null);
        _fallbackAvailable = fallbackAvailable ?? (static () => true);
        _leaveOpen = leaveOpen;
        LastUsedKind = Kind;
    }

    /// <inheritdoc />
    public TranscriberKind Kind => KindOf(_primary);

    /// <inheritdoc />
    public TranscriberKind LastUsedKind { get; private set; }

    /// <inheritdoc />
    public bool LastFellBack { get; private set; }

    /// <summary>Whether the primary is currently being skipped.</summary>
    public bool IsCircuitOpen
    {
        get
        {
            lock (_lock) return _networkDown || _time.GetUtcNow() < _openUntil;
        }
    }

    /// <summary>
    /// Raised each time a dictation is answered by the fallback, on the transcribing thread.
    /// </summary>
    public event EventHandler<TranscriptionFallback>? FellBack;

    /// <inheritdoc />
    public bool IsReady => _primary.IsReady || _fallback.IsReady;

    /// <inheritdoc />
    /// <remarks>
    /// Loads the primary only. The fallback is the expensive one to hold, and loading it
    /// before it is needed is what this arrangement exists to avoid.
    /// </remarks>
    public ValueTask<bool> LoadAsync(CancellationToken cancellationToken) =>
        _primary.LoadAsync(cancellationToken);

    /// <summary>Tells the breaker what the operating system says about the network.</summary>
    /// <param name="isAvailable">Whether any network is up.</param>
    /// <remarks>
    /// Down holds the circuit open for as long as it lasts. Up closes it at once, so the first
    /// dictation after reconnecting goes to the primary rather than waiting out the breaker.
    /// </remarks>
    public void OnNetworkAvailabilityChanged(bool isAvailable)
    {
        lock (_lock)
        {
            _networkDown = !isAvailable;
            if (isAvailable) _openUntil = DateTimeOffset.MinValue;
        }
    }

    /// <inheritdoc />
    public async ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken)
    {
        // Skipping the primary only makes sense when something else can answer.
        if (IsCircuitOpen && _fallbackAvailable())
        {
            return await UseFallbackAsync(samples, biasPhrases, cause: null, cancellationToken)
                .ConfigureAwait(false);
        }

        Exception? failure;

        // Built on the injected clock rather than CancelAfter, so the timeout is testable.
        using (var timeout = _primaryTimeout() is { } limit && limit > TimeSpan.Zero
                   ? new CancellationTokenSource(limit, _time)
                   : new CancellationTokenSource())
        using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
        {
            try
            {
                var text = await _primary.TranscribeAsync(samples, biasPhrases, attempt.Token)
                    .ConfigureAwait(false);

                LastUsedKind = KindOf(_primary);
                LastFellBack = false;
                return text;
            }
            catch (Exception ex) when (ShouldFallBack(ex, cancellationToken))
            {
                failure = ex;
            }
        }

        // Nothing to fall back to: the primary's own failure is the one the user needs to see,
        // and the circuit stays closed so the next dictation asks the primary again.
        if (!_fallbackAvailable()) throw AsPrimaryFailure(failure);

        lock (_lock) _openUntil = _time.GetUtcNow() + CircuitBreakDuration;

        return await UseFallbackAsync(samples, biasPhrases, failure, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<string> UseFallbackAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        Exception? cause,
        CancellationToken cancellationToken)
    {
        var text = await _fallback.TranscribeAsync(samples, biasPhrases, cancellationToken)
            .ConfigureAwait(false);

        LastUsedKind = KindOf(_fallback);
        LastFellBack = true;
        FellBack?.Invoke(this, new TranscriptionFallback(KindOf(_primary), LastUsedKind, cause));
        return text;
    }

    /// <summary>Whether <paramref name="failure"/> is one the fallback should answer.</summary>
    /// <param name="failure">What the primary threw.</param>
    /// <param name="callerToken">The caller's own token, to tell its cancellation from a timeout.</param>
    /// <returns>True for network trouble, timeouts, rate limits and server errors.</returns>
    public static bool ShouldFallBack(Exception failure, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // The user (or shutdown) cancelled: nothing more is wanted from anyone.
        if (callerToken.IsCancellationRequested) return false;

        return failure switch
        {
            TranscriptionException transcription => transcription.IsTransient,
            HttpRequestException http => http.StatusCode is not (
                HttpStatusCode.BadRequest or
                HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden or
                HttpStatusCode.NotFound),

            // With the caller's token untouched, a cancellation is the primary's own timeout
            // firing — HttpClient reports its timeout this way.
            OperationCanceledException or TimeoutException => true,
            _ => false,
        };
    }

    /// <summary>The primary's failure, as it should reach the caller when nothing could take over.</summary>
    /// <param name="failure">What the primary threw.</param>
    /// <returns>The exception to throw.</returns>
    /// <remarks>
    /// The primary's own timeout firing surfaces as a cancellation, which the caller would read
    /// as the user cancelling; it is reported as the timeout it was. Anything else is rethrown
    /// with its original stack.
    /// </remarks>
    private static Exception AsPrimaryFailure(Exception failure)
    {
        if (failure is OperationCanceledException or TimeoutException)
        {
            return new TranscriptionException(
                TranscriptionFailure.Timeout, "The cloud did not answer in time.", innerException: failure);
        }

        ExceptionDispatchInfo.Throw(failure);
        return failure; // Not reached.
    }

    private static TranscriberKind KindOf(ITranscriber transcriber) =>
        transcriber is ITranscriberDescriptor descriptor ? descriptor.Kind : TranscriberKind.Unknown;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_leaveOpen) return;

        await _primary.DisposeAsync().ConfigureAwait(false);
        await _fallback.DisposeAsync().ConfigureAwait(false);
    }
}
