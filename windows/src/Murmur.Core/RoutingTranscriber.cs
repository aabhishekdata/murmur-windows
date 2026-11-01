using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>
/// The one transcriber the dictation engine holds, sending each call to the engine the user
/// has chosen right now.
/// </summary>
/// <remarks>
/// <para>
/// The engine has exactly one transcriber slot, and that stays true. Switching engines is a
/// question asked per call through <c>mode</c>, so choosing a different engine in Settings
/// applies to the very next dictation, with no restart and no rebuilding of the engine.
/// </para>
/// <para>
/// It owns both underlying engines and disposes each once. The fallback arrangement is built
/// here over the same two instances, so there is only ever one Parakeet in the process no
/// matter which mode asks for it.
/// </para>
/// </remarks>
public sealed class RoutingTranscriber : ITranscriber, ITranscriberDescriptor
{
    private readonly Func<EngineMode> _mode;
    private readonly ITranscriber _offline;
    private readonly ITranscriber _cloud;

    /// <summary>Routes between an offline and a cloud engine.</summary>
    /// <param name="mode">The chosen engine, read on every call.</param>
    /// <param name="offline">Parakeet, normally wrapped in a <see cref="LazyTranscriber"/>.</param>
    /// <param name="cloud">The cloud engine.</param>
    /// <param name="time">Drives the fallback's circuit breaker and timeout.</param>
    /// <param name="cloudTimeout">
    /// How long the cloud may take in fallback mode before Parakeet is asked instead.
    /// </param>
    /// <param name="offlineAvailable">
    /// Whether Parakeet can answer at all — its model is installed. While it cannot, fallback mode
    /// keeps asking the cloud instead of skipping it. Null means always.
    /// </param>
    public RoutingTranscriber(
        Func<EngineMode> mode,
        ITranscriber offline,
        ITranscriber cloud,
        TimeProvider? time = null,
        Func<TimeSpan?>? cloudTimeout = null,
        Func<bool>? offlineAvailable = null)
    {
        _mode = mode;
        _offline = offline;
        _cloud = cloud;
        Fallback = new FallbackTranscriber(
            cloud, offline, time, cloudTimeout, leaveOpen: true, fallbackAvailable: offlineAvailable);
        LastUsedKind = Kind;
    }

    /// <summary>The cloud-then-Parakeet arrangement used in fallback mode.</summary>
    /// <remarks>Exposed so the app can subscribe to <see cref="FallbackTranscriber.FellBack"/>.</remarks>
    public FallbackTranscriber Fallback { get; }

    /// <summary>The mode in force right now.</summary>
    public EngineMode Mode => _mode();

    /// <summary>The engine a call made now would go to.</summary>
    public ITranscriber Current => Mode switch
    {
        EngineMode.Azure => _cloud,
        EngineMode.AzureWithParakeetFallback => Fallback,
        _ => _offline,
    };

    /// <summary>One of the two engines by itself, with no routing and no fallback.</summary>
    /// <param name="kind">Which.</param>
    /// <returns>The engine, or null for a kind this does not hold.</returns>
    /// <remarks>
    /// For History's retry, which wants the other engine and only that one. Whether it may be used
    /// at all is the caller's question (<see cref="TranscriptRetry.MayUse"/>).
    /// </remarks>
    public ITranscriber? For(TranscriberKind kind) => kind switch
    {
        TranscriberKind.Parakeet => _offline,
        TranscriberKind.Azure => _cloud,
        _ => null,
    };

    /// <inheritdoc />
    public TranscriberKind Kind => Current is ITranscriberDescriptor descriptor
        ? descriptor.Kind
        : TranscriberKind.Unknown;

    /// <inheritdoc />
    public TranscriberKind LastUsedKind { get; private set; }

    /// <inheritdoc />
    public bool LastFellBack { get; private set; }

    /// <inheritdoc />
    public bool IsReady => Current.IsReady;

    /// <inheritdoc />
    public ValueTask<bool> LoadAsync(CancellationToken cancellationToken) =>
        Current.LoadAsync(cancellationToken);

    /// <summary>
    /// Loads the offline engine now if the dictation about to happen is going to use it.
    /// </summary>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>Whether the offline engine was loaded; false when it was left alone.</returns>
    /// <remarks>
    /// Called when recording starts. In cloud-only mode it does nothing at all, which is what
    /// keeps Parakeet's weights out of memory there. In fallback mode it loads only while the
    /// circuit is open, when Parakeet is certain to answer; otherwise warming on every key
    /// press would keep the model resident for anyone who dictates more often than the idle
    /// timeout, for a fallback the cloud may never need. A fallback that is needed while the
    /// circuit is closed loads on demand instead.
    /// </remarks>
    public async Task<bool> WarmUpAsync(CancellationToken cancellationToken)
    {
        var mode = Mode;
        var willAnswer = EngineStatus.LoadsOfflineAtStartup(mode)
            || (mode == EngineMode.AzureWithParakeetFallback && Fallback.IsCircuitOpen);
        if (!willAnswer) return false;
        return await _offline.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken)
    {
        var target = Current;
        var text = await target.TranscribeAsync(samples, biasPhrases, cancellationToken)
            .ConfigureAwait(false);

        if (target is ITranscriberDescriptor descriptor)
        {
            LastUsedKind = descriptor.LastUsedKind;
            LastFellBack = descriptor.LastFellBack;
        }
        else
        {
            LastUsedKind = TranscriberKind.Unknown;
            LastFellBack = false;
        }

        return text;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Fallback.DisposeAsync().ConfigureAwait(false);
        await _offline.DisposeAsync().ConfigureAwait(false);
        await _cloud.DisposeAsync().ConfigureAwait(false);
    }
}
