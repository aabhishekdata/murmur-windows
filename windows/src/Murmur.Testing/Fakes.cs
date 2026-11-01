using System.Runtime.CompilerServices;
using Murmur.Abstractions;

namespace Murmur.Testing;

/// <summary>
/// Test doubles for the four platform interfaces.
/// </summary>
/// <remarks>
/// These are what make the Windows app's behaviour verifiable from a machine that is not
/// Windows. Everything interesting — the state machine, chunking, the correction pass, the
/// silence case — runs against these in milliseconds, on any platform, in CI.
/// </remarks>
public sealed class FakeAudioCapture : IAudioCapture
{
    private readonly float[] _samples;
    private readonly int _chunkSize;

    /// <summary>Replays a fixed buffer as a series of chunks.</summary>
    /// <param name="samples">16 kHz mono float.</param>
    /// <param name="chunkSize">Samples per chunk; defaults to 20 ms.</param>
    public FakeAudioCapture(float[] samples, int chunkSize = AudioChunk.SampleRate / 50)
    {
        _samples = samples;
        _chunkSize = Math.Max(1, chunkSize);
    }

    /// <summary>Generates <paramref name="seconds"/> of a steady tone at <paramref name="amplitude"/>.</summary>
    public static FakeAudioCapture Tone(double seconds, float amplitude = 0.5f)
    {
        var count = (int)(seconds * AudioChunk.SampleRate);
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = amplitude * MathF.Sin(2 * MathF.PI * 440 * i / AudioChunk.SampleRate);
        }
        return new FakeAudioCapture(samples);
    }

    /// <summary>Generates <paramref name="seconds"/> of digital silence.</summary>
    public static FakeAudioCapture Silence(double seconds) =>
        new(new float[(int)(seconds * AudioChunk.SampleRate)]);

    /// <inheritdoc />
    public bool IsCapturing { get; private set; }

    /// <inheritdoc />
    public async IAsyncEnumerable<AudioChunk> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IsCapturing = true;
        try
        {
            for (var offset = 0; offset < _samples.Length; offset += _chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(_chunkSize, _samples.Length - offset);

                // A fresh array per chunk, deliberately: a fake that reuses one buffer would
                // hide the very aliasing bug real capture implementations are prone to.
                var chunk = new float[length];
                Array.Copy(_samples, offset, chunk, 0, length);
                yield return new AudioChunk(chunk);

                await Task.Yield();
            }
        }
        finally
        {
            IsCapturing = false;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A hotkey you press from a test.</summary>
public sealed class FakeHotkeySource : IHotkeySource, ICancelKeySource
{
    /// <inheritdoc />
    public event EventHandler? CancelPressed;

    /// <inheritdoc />
    public bool CancelArmed { get; set; }

    /// <summary>Presses Esc as the hook would: raised, and swallowed, only while armed.</summary>
    /// <returns>Whether the press was swallowed.</returns>
    public bool PressEscape()
    {
        if (!CancelArmed) return false;
        CancelPressed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public event EventHandler? Pressed;

    /// <inheritdoc />
    public event EventHandler? Released;

    /// <summary>Whether <see cref="Start"/> has been called.</summary>
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public int VirtualKey { get; set; } = 0xA3;

    /// <inheritdoc />
    public bool Start() { IsRunning = true; return true; }

    /// <inheritdoc />
    public void StopListening() => IsRunning = false;

    /// <summary>Raises <see cref="Pressed"/>.</summary>
    public void Press() => Pressed?.Invoke(this, EventArgs.Empty);

    /// <summary>Raises <see cref="Released"/>.</summary>
    public void Release() => Released?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public void Dispose() => StopListening();
}

/// <summary>Returns canned transcripts and records what it was asked to transcribe.</summary>
public sealed class FakeTranscriber : ITranscriber
{
    private readonly Queue<string> _responses;

    /// <summary>Each call returns the next response; the last repeats once exhausted.</summary>
    public FakeTranscriber(params string[] responses) => _responses = new Queue<string>(responses);

    /// <summary>How many segments were submitted. Reveals chunking behaviour.</summary>
    public List<int> SegmentLengths { get; } = [];

    /// <summary>The bias list handed over on the most recent call.</summary>
    public IReadOnlyList<string> LastBias { get; private set; } = [];

    /// <inheritdoc />
    public bool IsReady { get; private set; }

    /// <inheritdoc />
    public ValueTask<bool> LoadAsync(CancellationToken cancellationToken)
    {
        IsReady = true;
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken)
    {
        SegmentLengths.Add(samples.Length);
        LastBias = biasPhrases;
        var text = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
        return ValueTask.FromResult(text);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Records what would have been typed.
/// </summary>
/// <remarks>
/// Asserting on intent rather than on an OS effect is deliberate: whether a keystroke really
/// lands in another application's text field is the one thing CI cannot observe, because
/// runners cannot take the foreground.
/// </remarks>
public sealed class RecordingTextInjector : ITextInjector
{
    /// <summary>Everything injected, in order.</summary>
    public List<string> Injected { get; } = [];

    /// <summary>How many times Enter was pressed.</summary>
    public int EnterPresses { get; private set; }

    /// <summary>
    /// How many characters had been injected by the time Enter was first pressed.
    /// </summary>
    /// <remarks>
    /// Order is the thing worth asserting about a submit: pressing Enter before the text has
    /// landed sends an empty message, which is a far more visible failure than not sending one.
    /// </remarks>
    public int InjectedCountAtFirstEnter { get; private set; } = -1;

    /// <summary>For each entry in <see cref="Injected"/>, whether it was asked for verbatim.</summary>
    public List<bool> Verbatim { get; } = [];

    /// <inheritdoc />
    public ValueTask<bool> InjectAsync(string text, CancellationToken cancellationToken) =>
        InjectAsync(text, verbatim: false, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> InjectAsync(string text, bool verbatim, CancellationToken cancellationToken)
    {
        Injected.Add(text);
        Verbatim.Add(verbatim);
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask<bool> PressEnterAsync(CancellationToken cancellationToken)
    {
        if (EnterPresses == 0) InjectedCountAtFirstEnter = Injected.Count;
        EnterPresses++;
        return ValueTask.FromResult(true);
    }
}

/// <summary>A microphone list you write by hand.</summary>
public sealed class FakeAudioDeviceCatalog : IAudioDeviceCatalog
{
    private readonly IReadOnlyList<AudioDeviceInfo> _devices;

    /// <summary>Reports the given devices.</summary>
    /// <param name="devices">What <see cref="List"/> should return.</param>
    public FakeAudioDeviceCatalog(params AudioDeviceInfo[] devices) => _devices = devices;

    /// <inheritdoc />
    public IReadOnlyList<AudioDeviceInfo> List() => _devices;
}

/// <summary>A clock you advance by hand.</summary>
public sealed class FakeClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire only when the test advances it.
/// </summary>
/// <remarks>
/// Anything debounced is tested against this rather than against <c>Task.Delay</c>: a test
/// that sleeps to let a real timer fire is a test that fails on a slow CI runner. Timer
/// callbacks run synchronously inside <see cref="Advance"/>, on the test's own thread.
/// </remarks>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <inheritdoc />
    public override ITimer CreateTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves time forward, firing every timer that comes due on the way.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by)
    {
        var target = _now + by;

        while (true)
        {
            ManualTimer? next;
            lock (_timers)
            {
                next = _timers
                    .Where(t => t.DueAt is { } due && due <= target)
                    .OrderBy(t => t.DueAt)
                    .FirstOrDefault();
            }

            if (next is null) break;

            _now = next.DueAt!.Value;
            next.Fire();
        }

        _now = target;
    }

    private void Remove(ManualTimer timer)
    {
        lock (_timers) _timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            _period = period;
            return true;
        }

        public void Fire()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero
                ? null
                : DueAt + _period;
            callback(state);
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A transcriber that follows a script: answer, throw, or hang, one step per call.
/// </summary>
/// <remarks>
/// For the failure paths <see cref="FakeTranscriber"/> cannot reach — timeouts, fallbacks,
/// cancellation and unloading under a decode. Each call takes the next step; the last step
/// repeats once the script runs out, and an empty script answers with an empty string.
/// </remarks>
public sealed class ScriptedTranscriber : ITranscriber, ITranscriberDescriptor
{
    private readonly Queue<Func<CancellationToken, Task<string>>> _steps = new();
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _calls = new(0);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<CancellationToken, Task<string>>? _last;
    private int _inFlight;

    /// <summary>Creates an empty script.</summary>
    /// <param name="kind">What engine this pretends to be.</param>
    public ScriptedTranscriber(TranscriberKind kind = TranscriberKind.Unknown) => Kind = kind;

    /// <inheritdoc />
    public TranscriberKind Kind { get; }

    /// <summary>What <see cref="LoadAsync"/> answers. True by default.</summary>
    public bool LoadResult { get; set; } = true;

    /// <inheritdoc />
    public bool IsReady { get; private set; }

    /// <summary>How many times <see cref="LoadAsync"/> was called.</summary>
    public int LoadCalls { get; private set; }

    /// <summary>How many times <see cref="TranscribeAsync"/> was called.</summary>
    public int Calls { get; private set; }

    /// <summary>How many times <see cref="DisposeAsync"/> was called.</summary>
    public int DisposeCalls { get; private set; }

    /// <summary>Whether it was disposed while a call was still running — the bug to catch.</summary>
    public bool DisposedWhileInFlight { get; private set; }

    /// <summary>Whether any call saw its token cancelled.</summary>
    public bool ObservedCancellation { get; private set; }

    /// <summary>Answers <paramref name="text"/>.</summary>
    public ScriptedTranscriber Returns(string text) => Then(_ => Task.FromResult(text));

    /// <summary>Throws <paramref name="exception"/>.</summary>
    public ScriptedTranscriber Throws(Exception exception) => Then(_ => Task.FromException<string>(exception));

    /// <summary>Waits until the call is cancelled, then throws as a well-behaved engine would.</summary>
    public ScriptedTranscriber Hangs() => Then(async token =>
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ObservedCancellation = true;
            throw;
        }

        return string.Empty;
    });

    /// <summary>
    /// Ignores its token and waits for <see cref="Release"/>, then answers <paramref name="text"/>.
    /// </summary>
    /// <remarks>The shape of Parakeet's native decode, which cannot be interrupted.</remarks>
    public ScriptedTranscriber HangsUntilReleased(string text) => Then(async _ =>
    {
        await _released.Task.ConfigureAwait(false);
        return text;
    });

    /// <summary>Lets every <see cref="HangsUntilReleased"/> step finish.</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>Waits for the next call to begin.</summary>
    /// <param name="timeout">How long to wait; ten seconds when omitted.</param>
    /// <returns>True if a call began in time.</returns>
    public Task<bool> WaitForCallAsync(TimeSpan? timeout = null) =>
        _calls.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));

    private ScriptedTranscriber Then(Func<CancellationToken, Task<string>> step)
    {
        lock (_lock) _steps.Enqueue(step);
        return this;
    }

    /// <inheritdoc />
    public ValueTask<bool> LoadAsync(CancellationToken cancellationToken)
    {
        LoadCalls++;
        IsReady = LoadResult;
        return ValueTask.FromResult(LoadResult);
    }

    /// <inheritdoc />
    public async ValueTask<string> TranscribeAsync(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<string> biasPhrases,
        CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task<string>> step;
        lock (_lock)
        {
            Calls++;
            if (_steps.Count > 0) _last = _steps.Dequeue();
            step = _last ?? (static _ => Task.FromResult(string.Empty));
        }

        Interlocked.Increment(ref _inFlight);
        _calls.Release();
        try
        {
            return await step(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        if (Volatile.Read(ref _inFlight) > 0) DisposedWhileInFlight = true;
        IsReady = false;
        return ValueTask.CompletedTask;
    }
}
