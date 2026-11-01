using System.Runtime.CompilerServices;
using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>
/// Regression tests for the completion pipeline, driven from real background threads.
/// </summary>
/// <remarks>
/// <para>
/// The existing tests raise hotkey events synchronously from the test's own thread, which is
/// not how the app runs: the Windows keyboard hook fires <c>Released</c> on its own dedicated
/// thread, and the whole transcribe → complete → inject chain stays on background threads via
/// <c>ConfigureAwait(false)</c>. Two shipped bugs lived precisely in that gap.
/// </para>
/// <para>
/// Everything here therefore presses and releases on a genuine <see cref="Thread"/> — the
/// same shape as <c>PushToTalkHook</c>'s "Murmur push-to-talk hook" thread — and asserts both
/// that no exception escapes onto that thread (in production it would be swallowed or would
/// corrupt the hook) and that the completion side-effects actually happen.
/// </para>
/// </remarks>
public sealed class CompletionPipelineTests
{
    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Murmur", "last-crash.log");

    /// <summary>Reads the crash log while other tests may be appending to it.</summary>
    /// <remarks>
    /// <see cref="File.ReadAllText(string)"/> refuses a file another handle has open for
    /// writing, and the test assemblies run in parallel against the same log.
    /// </remarks>
    private static string ReadCrashLog()
    {
        using var stream = new FileStream(
            CrashLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a dedicated thread — the shape of the real keyboard
    /// hook thread — and fails the test if anything escapes onto it.
    /// </summary>
    private static void OnHookThread(Action action)
    {
        Exception? escaped = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { escaped = ex; }
        })
        {
            IsBackground = true,
            Name = "test push-to-talk hook",
        };

        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue("the hook thread must not hang");
        escaped.ShouldBeNull($"nothing may escape onto the hook thread, but got: {escaped}");
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        condition().ShouldBeTrue(because);
    }

    /// <summary>Presses, lets the fake capture drain completely, then releases — all off-thread.</summary>
    private static async Task DictateFromBackgroundAsync(
        FakeHotkeySource hotkey, TrackingCapture capture, DictationEngine engine)
    {
        var before = capture.DrainedCount;
        OnHookThread(hotkey.Press);
        await WaitForAsync(() => capture.DrainedCount > before, "the fake capture should drain");

        OnHookThread(hotkey.Release);
        await WaitForAsync(() => engine.State == DictationState.Idle, "the engine should return to idle");
    }

    /// <summary>
    /// Wraps a capture source and counts fully-consumed enumerations.
    /// </summary>
    /// <remarks>
    /// Polling <see cref="FakeAudioCapture.IsCapturing"/> is racy twice over: a short fake
    /// buffer drains between two polls (the "went true" observation is missed entirely), and
    /// on back-to-back dictations a boolean latch would still be true from the previous
    /// round. A per-enumeration counter is race-free.
    /// </remarks>
    private sealed class TrackingCapture(IAudioCapture inner) : IAudioCapture
    {
        private int _drained;

        public int DrainedCount => Volatile.Read(ref _drained);

        public bool IsCapturing => inner.IsCapturing;

        public async IAsyncEnumerable<AudioChunk> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var chunk in inner.CaptureAsync(cancellationToken)) yield return chunk;
            Interlocked.Increment(ref _drained);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task Dictation_driven_from_a_real_background_thread_completes_and_injects()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new TrackingCapture(FakeAudioCapture.Tone(0.5));
        var injector = new RecordingTextInjector();

        await using var engine = new DictationEngine(
            capture, hotkey, new FakeTranscriber("hello from the hook thread"), injector, () => []);

        var completed = 0;
        engine.Completed += (_, _) => Interlocked.Increment(ref completed);

        await DictateFromBackgroundAsync(hotkey, capture, engine);

        injector.Injected.ShouldBe(["hello from the hook thread"]);
        completed.ShouldBe(1);
        engine.State.ShouldBe(DictationState.Idle);
    }

    [Fact]
    public async Task Rapid_back_to_back_dictations_neither_wedge_the_state_machine_nor_drop_text()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        var transcriber = new FakeTranscriber("one", "two", "three", "four", "five");

        // One capture instance replayed five times, exactly as the real device would be.
        var capture = new TrackingCapture(FakeAudioCapture.Tone(0.3));
        await using var engine = new DictationEngine(capture, hotkey, transcriber, injector, () => []);

        var completed = 0;
        engine.Completed += (_, _) => Interlocked.Increment(ref completed);

        for (var i = 0; i < 5; i++)
        {
            await DictateFromBackgroundAsync(hotkey, capture, engine);
        }

        injector.Injected.ShouldBe(["one", "two", "three", "four", "five"]);
        completed.ShouldBe(5);
        engine.State.ShouldBe(DictationState.Idle);
    }

    [Fact]
    public async Task A_throwing_Completed_subscriber_is_logged_and_never_blocks_injection()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new TrackingCapture(FakeAudioCapture.Tone(0.4));
        var injector = new RecordingTextInjector();

        await using var engine = new DictationEngine(
            capture, hotkey, new FakeTranscriber("still typed"), injector, () => []);

        // The shipped bug in miniature: the history subscriber blowing up (there it was an
        // Avalonia cross-thread violation) must not stop the text reaching the focused app.
        var marker = Guid.NewGuid().ToString("N");
        engine.Completed += (_, _) => throw new InvalidOperationException($"subscriber boom {marker}");

        await DictateFromBackgroundAsync(hotkey, capture, engine);

        injector.Injected.ShouldBe(["still typed"], "injection must not depend on subscribers");
        engine.State.ShouldBe(DictationState.Idle);

        // And it must be a logged failure, not a silently swallowed one.
        File.Exists(CrashLogPath).ShouldBeTrue("the failure must be recorded");
        ReadCrashLog().ShouldContain(marker);
    }

    [Fact]
    public async Task A_failure_anywhere_in_processing_is_logged_and_returns_the_engine_to_idle()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new TrackingCapture(FakeAudioCapture.Tone(0.4));
        var injector = new RecordingTextInjector();
        var marker = Guid.NewGuid().ToString("N");

        await using var engine = new DictationEngine(
            capture, hotkey, new ThrowingTranscriber($"model exploded {marker}"), injector, () => []);

        await DictateFromBackgroundAsync(hotkey, capture, engine);

        injector.Injected.ShouldBeEmpty();
        File.Exists(CrashLogPath).ShouldBeTrue("the failure must be recorded");
        ReadCrashLog().ShouldContain(marker);

        // And the engine is still usable afterwards — the failure was one dictation, not the app.
        engine.State.ShouldBe(DictationState.Idle);
    }

    [Fact]
    public async Task Long_audio_is_transcribed_in_segments_but_completes_and_injects_exactly_once()
    {
        var hotkey = new FakeHotkeySource();
        var transcriber = new FakeTranscriber("segment");
        var injector = new RecordingTextInjector();

        // 70 s of tone: past MaxSegmentSeconds (60), so AudioSegmenter must split it.
        var capture = new TrackingCapture(FakeAudioCapture.Tone(70));
        await using var engine = new DictationEngine(capture, hotkey, transcriber, injector, () => []);

        var completed = new List<DictationResult>();
        engine.Completed += (_, r) => { lock (completed) completed.Add(r); };

        await DictateFromBackgroundAsync(hotkey, capture, engine);

        transcriber.SegmentLengths.Count.ShouldBe(2, "70 s must be split into two segments");
        transcriber.SegmentLengths.Sum().ShouldBe(70 * AudioChunk.SampleRate);

        completed.Count.ShouldBe(1, "Completed fires once per dictation, not per segment");
        completed[0].Text.ShouldBe("segment segment");
        injector.Injected.ShouldBe(["segment segment"], "one injection carrying all segments");
    }

    [Fact]
    public async Task Disposal_racing_an_in_flight_completion_neither_hangs_nor_throws()
    {
        var hotkey = new FakeHotkeySource();
        var capture = new TrackingCapture(FakeAudioCapture.Tone(0.3));
        var injector = new RecordingTextInjector();
        var transcriber = new SlowTranscriber("late text");

        var engine = new DictationEngine(capture, hotkey, transcriber, injector, () => []);

        OnHookThread(hotkey.Press);
        await WaitForAsync(() => capture.DrainedCount > 0, "the fake capture should drain");
        OnHookThread(hotkey.Release);
        await WaitForAsync(() => transcriber.Started, "transcription should be in flight");

        // The app shuts down while a dictation is still being transcribed.
        await engine.DisposeAsync();

        // Now the transcription finishes. The completion path must run to termination
        // without throwing onto its background thread or wedging the state machine.
        transcriber.Finish();
        await WaitForAsync(() => engine.State == DictationState.Idle,
            "the in-flight dictation must still unwind to idle");

        // Disposal cancels the dictation: text arriving after the app has shut down must not
        // be typed into whatever has focus by then.
        injector.Injected.ShouldBeEmpty();
    }

    /// <summary>A transcriber whose model blows up — exercises the EndAsync catch.</summary>
    private sealed class ThrowingTranscriber(string message) : ITranscriber
    {
        public bool IsReady => true;

        public ValueTask<bool> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<string> TranscribeAsync(
            ReadOnlyMemory<float> samples,
            IReadOnlyList<string> biasPhrases,
            CancellationToken cancellationToken) => throw new InvalidOperationException(message);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A transcriber that holds its answer until the test lets go.</summary>
    private sealed class SlowTranscriber(string text) : ITranscriber
    {
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Started { get; private set; }

        public bool IsReady => true;

        public void Finish() => _gate.SetResult();

        public ValueTask<bool> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public async ValueTask<string> TranscribeAsync(
            ReadOnlyMemory<float> samples,
            IReadOnlyList<string> biasPhrases,
            CancellationToken cancellationToken)
        {
            Started = true;
            await _gate.Task.ConfigureAwait(false);
            return text;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
