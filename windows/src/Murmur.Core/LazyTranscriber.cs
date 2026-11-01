using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>
/// Loads a transcriber on first use, and unloads it again after it has sat idle.
/// </summary>
/// <remarks>
/// <para>
/// Parakeet holds roughly 660 MB of weights once loaded. When it is the engine, that is the
/// price of dictation and it is paid at startup, as it always was — the <c>keepWarm</c> callback
/// says so. When it is only the fallback behind a cloud engine, paying it for the whole life
/// of the process, for a failure that may never happen, is exactly the memory and CPU the
/// cloud engine was chosen to save. So it loads when needed and goes away when not.
/// </para>
/// <para>
/// <b>Load, decode and unload never overlap.</b> One semaphore guards all three. An unload
/// that comes due mid-decode waits for the decode to finish, then sees the model was just used
/// and stays its hand; a dictation that arrives mid-load waits for the load rather than
/// meeting a half-built recogniser. Disposing the native recogniser under a running decode
/// would be a use-after-free in native code, not an exception.
/// </para>
/// <para>
/// A fresh instance is built from the factory on every load, rather than one instance being
/// disposed and revived, so settings read by the factory — the thread count, the model folder
/// — apply on the next load without a restart.
/// </para>
/// </remarks>
public sealed class LazyTranscriber : ITranscriber, ITranscriberDescriptor
{
    /// <summary>How long it may sit unused when nothing says otherwise.</summary>
    public static readonly TimeSpan DefaultUnloadAfter = TimeSpan.FromMinutes(10);

    /// <summary>How long disposal waits for a decode or load in flight when nothing says otherwise.</summary>
    public static readonly TimeSpan DefaultDisposeWait = TimeSpan.FromSeconds(5);

    private readonly Func<ITranscriber?> _factory;
    private readonly Func<bool> _keepWarm;
    private readonly Func<TimeSpan> _unloadAfter;
    private readonly TimeProvider _time;
    private readonly TimeSpan _disposeWait;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ITimer _idle;

    private ITranscriber? _inner;
    private DateTimeOffset _lastUsed;
    private volatile bool _disposed;

    /// <summary>Wraps a transcriber that is built on demand.</summary>
    /// <param name="factory">
    /// Builds an unloaded transcriber, or returns null when it cannot — no model on disk, say.
    /// Called on every load.
    /// </param>
    /// <param name="kind">What the built transcriber is.</param>
    /// <param name="keepWarm">
    /// Read each time the idle timer fires; while it answers true nothing is unloaded.
    /// Defaults to never.
    /// </param>
    /// <param name="unloadAfter">How long it may sit unused; read each time it is used.</param>
    /// <param name="time">Drives the idle timer; defaults to the system clock.</param>
    /// <param name="disposeWait">
    /// How long <see cref="DisposeAsync"/> waits for a decode or load in flight before giving up
    /// on it; defaults to <see cref="DefaultDisposeWait"/>.
    /// </param>
    public LazyTranscriber(
        Func<ITranscriber?> factory,
        TranscriberKind kind,
        Func<bool>? keepWarm = null,
        Func<TimeSpan>? unloadAfter = null,
        TimeProvider? time = null,
        TimeSpan? disposeWait = null)
    {
        _factory = factory;
        Kind = kind;
        _keepWarm = keepWarm ?? (static () => false);
        _unloadAfter = unloadAfter ?? (static () => DefaultUnloadAfter);
        _time = time ?? TimeProvider.System;
        _disposeWait = disposeWait ?? DefaultDisposeWait;

        _idle = _time.CreateTimer(
            static state => _ = ((LazyTranscriber)state!).UnloadIfIdleAsync(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public TranscriberKind Kind { get; }

    /// <inheritdoc />
    public bool IsReady => Volatile.Read(ref _inner)?.IsReady == true;

    /// <summary>Whether a transcriber is currently built and held.</summary>
    public bool IsLoaded => Volatile.Read(ref _inner) is not null;

    /// <summary>Raised after the model has been unloaded for sitting idle. May fire on any thread.</summary>
    public event EventHandler? Unloaded;

    /// <inheritdoc />
    /// <remarks>Also starts the idle clock, so a warm-up that is never used still unloads.</remarks>
    public async ValueTask<bool> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            Touch();
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads now, ahead of the dictation that is about to need it.</summary>
    /// <param name="cancellationToken">Abandons the wait for the gate.</param>
    /// <returns>Whether the transcriber is loaded.</returns>
    /// <remarks>
    /// Called when the push-to-talk key goes down, so the load overlaps the user speaking
    /// instead of being added to the wait after they stop.
    /// </remarks>
    public Task<bool> WarmUpAsync(CancellationToken cancellationToken) =>
        LoadAsync(cancellationToken).AsTask();

    /// <inheritdoc />
    /// <exception cref="TranscriptionException">
    /// <see cref="TranscriptionFailure.NoModel"/> when there is nothing to load or it would not load.
    /// </exception>
    public async ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new TranscriptionException(
                    TranscriptionFailure.NoModel, "The offline speech model is not installed.");
            }

            return await _inner!.TranscribeAsync(samples, biasPhrases, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Nested, so nothing Touch does can keep the gate from being released.
            try
            {
                Touch();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>Call with the gate held.</summary>
    private async ValueTask<bool> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_inner is not null) return _inner.IsReady;

        var built = _factory();
        if (built is null) return false;

        bool loaded;
        try
        {
            loaded = await built.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await built.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (!loaded)
        {
            // Not kept: a model that would not load is retried from scratch next time, which
            // is what lets a download that finished in the meantime simply start working.
            await built.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        Volatile.Write(ref _inner, built);
        return true;
    }

    /// <summary>Records a use and restarts the idle clock. Call with the gate held.</summary>
    private void Touch()
    {
        _lastUsed = _time.GetUtcNow();
        if (_inner is not null) RestartIdle(UnloadAfter());
    }

    /// <summary>Arms the idle timer, unless disposal has already taken it away.</summary>
    /// <remarks>
    /// <see cref="_disposed"/> is set under the gate, so with the gate held the check is exact,
    /// except after a disposal that gave up waiting for a decode and disposed the timer while
    /// that decode still held the gate. The system timer answers false when changed after
    /// disposal, but <see cref="ITimer"/> does not promise that, so the catch makes sure no
    /// timer can turn a finished decode into one that never releases the gate.
    /// </remarks>
    private void RestartIdle(TimeSpan dueTime)
    {
        if (_disposed) return;
        try
        {
            _idle.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down; there is nothing left to unload for.
        }
    }

    private TimeSpan UnloadAfter()
    {
        var after = _unloadAfter();
        return after > TimeSpan.Zero ? after : DefaultUnloadAfter;
    }

    private async Task UnloadIfIdleAsync()
    {
        try
        {
            // No token: this is the timer's own thread, and nothing waits on it. It queues
            // behind a decode in flight rather than interrupting it.
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        var unloaded = false;
        try
        {
            if (_disposed || _inner is null) return;

            var idleFor = _time.GetUtcNow() - _lastUsed;
            var after = UnloadAfter();

            if (_keepWarm())
            {
                // Looked at again later rather than never: switching keep-warm off, or
                // switching the engine away from Parakeet, must eventually let it go even
                // if it is never used again.
                RestartIdle(after);
                return;
            }

            if (idleFor < after)
            {
                // Used while this callback was queued behind it. The use restarted the clock.
                RestartIdle(after - idleFor);
                return;
            }

            var inner = _inner;
            Volatile.Write(ref _inner, null);
            await inner.DisposeAsync().ConfigureAwait(false);
            unloaded = true;
        }
        finally
        {
            _gate.Release();
        }

        if (unloaded) Unloaded?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Waits for a decode or load in flight before disposing what it is using, and takes the
    /// gate before disposing the idle timer, so a decode that finishes meanwhile restarts a
    /// timer that is still there rather than throwing on a disposed one.
    /// </para>
    /// <para>
    /// The wait is bounded. This runs on the UI thread when the app quits, and a decode that
    /// never returns is exactly what the dictation timeout abandons. If the wait runs out, the
    /// model is left loaded rather than freed under that decode — a use-after-free in native
    /// code — since the process is exiting and the operating system reclaims it anyway.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        var held = await _gate.WaitAsync(_disposeWait).ConfigureAwait(false);
        try
        {
            _disposed = true;
            await _idle.DisposeAsync().ConfigureAwait(false);

            if (held && _inner is { } inner)
            {
                Volatile.Write(ref _inner, null);
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (held) _gate.Release();
        }

        // The gate is deliberately not disposed: an idle callback already queued on it must
        // be able to take it, see _disposed, and leave, rather than throw on a timer thread.
    }
}
