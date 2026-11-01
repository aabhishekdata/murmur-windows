using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>
/// Every wait the engine makes now has an end: transcription, typing, and shutdown.
/// </summary>
/// <remarks>
/// Every call used to pass <see cref="CancellationToken.None"/>. A transcriber that never
/// answered left the engine in Transcribing for good, and since a recording only starts from
/// Idle, the push-to-talk key was dead until the app was restarted.
/// </remarks>
public sealed class EngineTimeoutTests
{
    /// <summary>A budget with no per-second part, so the limit does not depend on how much audio landed.</summary>
    private static DictationEngineOptions Limits(ManualTimeProvider time) => new()
    {
        TranscriptionBaseBudget = TimeSpan.FromSeconds(10),
        TranscriptionBudgetPerAudioSecond = 0,
        InjectionTimeout = TimeSpan.FromSeconds(10),
        Time = time,
    };

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        condition().ShouldBeTrue(because);
    }

    /// <summary>Presses, waits for audio, releases. Does not wait for the result.</summary>
    private static async Task SpeakAsync(FakeHotkeySource hotkey, DictationEngine engine)
    {
        hotkey.Press();
        await WaitForAsync(() => engine.Level > 0, "audio should arrive");
        hotkey.Release();
        await WaitForAsync(() => engine.State != DictationState.Recording, "release should end recording");
    }

    [Fact]
    public void The_budget_is_ten_seconds_plus_twice_the_audio()
    {
        var limits = new DictationEngineOptions();

        limits.TranscriptionBudget(TimeSpan.Zero).ShouldBe(TimeSpan.FromSeconds(10));
        limits.TranscriptionBudget(TimeSpan.FromSeconds(30)).ShouldBe(TimeSpan.FromSeconds(70));
        limits.InjectionTimeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_hanging_transcriber_hits_the_budget_and_the_engine_returns_to_idle()
    {
        var time = new ManualTimeProvider();
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        var transcriber = new ScriptedTranscriber().Hangs();

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(30), hotkey, transcriber, injector, () => [], limits: Limits(time));

        Exception? fault = null;
        engine.TranscriptionFaulted += (_, ex) => fault = ex;

        await SpeakAsync(hotkey, engine);
        (await transcriber.WaitForCallAsync()).ShouldBeTrue();

        time.Advance(TimeSpan.FromSeconds(9.9));
        engine.State.ShouldBe(DictationState.Transcribing, "still inside the budget");

        time.Advance(TimeSpan.FromSeconds(0.1));
        await WaitForAsync(() => engine.State == DictationState.Idle, "the budget ran out");

        fault.ShouldBeOfType<TranscriptionException>().Failure.ShouldBe(TranscriptionFailure.Timeout);
        await WaitForAsync(() => transcriber.ObservedCancellation, "the transcriber is told to stop");
        injector.Injected.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_transcriber_that_ignores_cancellation_still_cannot_hold_the_engine()
    {
        // Parakeet's decode is native and cannot be interrupted at all.
        var time = new ManualTimeProvider();
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        var transcriber = new ScriptedTranscriber().HangsUntilReleased("too late").Returns("next");

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(30), hotkey, transcriber, injector, () => [], limits: Limits(time));

        await SpeakAsync(hotkey, engine);
        (await transcriber.WaitForCallAsync()).ShouldBeTrue();

        time.Advance(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => engine.State == DictationState.Idle, "abandoned, not waited for");

        // The late answer is dropped, and the key works again straight away.
        transcriber.Release();
        await SpeakAsync(hotkey, engine);
        await WaitForAsync(() => engine.State == DictationState.Idle, "the next dictation completes");
        injector.Injected.ShouldBe(["next"]);
    }

    [Fact]
    public async Task Dispose_cancels_a_dictation_in_flight_without_reporting_a_fault()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        var transcriber = new ScriptedTranscriber().Hangs();

        var engine = new DictationEngine(
            FakeAudioCapture.Tone(30), hotkey, transcriber, injector, () => [],
            limits: Limits(new ManualTimeProvider()));

        var faults = 0;
        engine.TranscriptionFaulted += (_, _) => faults++;

        await SpeakAsync(hotkey, engine);
        (await transcriber.WaitForCallAsync()).ShouldBeTrue();

        await engine.DisposeAsync();

        await WaitForAsync(() => transcriber.ObservedCancellation, "disposal cancels the work");
        await WaitForAsync(() => engine.State == DictationState.Idle, "and the engine unwinds");
        faults.ShouldBe(0, "shutting down is not a failure to tell anyone about");
        injector.Injected.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_transcription_failure_is_raised_and_the_key_keeps_working()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        var noModel = new TranscriptionException(TranscriptionFailure.NoModel, "not installed");
        var transcriber = new ScriptedTranscriber().Throws(noModel).Returns("works now");

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(30), hotkey, transcriber, injector, () => []);

        var faults = new List<Exception>();
        engine.TranscriptionFaulted += (_, ex) => { lock (faults) faults.Add(ex); };

        await SpeakAsync(hotkey, engine);
        await WaitForAsync(() => engine.State == DictationState.Idle, "a failure ends the dictation");
        faults.ShouldBe([noModel]);

        await SpeakAsync(hotkey, engine);
        await WaitForAsync(() => engine.State == DictationState.Idle, "the next one completes");
        injector.Injected.ShouldBe(["works now"]);
    }

    [Fact]
    public async Task Typing_that_hangs_times_out_and_Enter_is_not_pressed_after_it()
    {
        var time = new ManualTimeProvider();
        var hotkey = new FakeHotkeySource();
        var injector = new HangingInjector();

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(30), hotkey, new FakeTranscriber("Send this. Press enter."),
            injector, () => [], limits: Limits(time));

        await SpeakAsync(hotkey, engine);
        (await injector.Typing.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken)).ShouldBeTrue();

        time.Advance(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => engine.State == DictationState.Idle, "the injection timeout fired");

        await WaitForAsync(() => injector.ObservedCancellation, "the injector is told to stop");
        injector.EnterPresses.ShouldBe(0, "half-typed text must not be submitted");
    }

    [Fact]
    public async Task The_result_records_which_engine_heard_it_and_whether_it_fell_back()
    {
        var hotkey = new FakeHotkeySource();
        var mode = EngineMode.AzureWithParakeetFallback;
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("heard locally");
        var cloud = new ScriptedTranscriber(TranscriberKind.Azure)
            .Throws(new TranscriptionException(TranscriptionFailure.Network, "offline"))
            .Returns("heard remotely");
        var time = new ManualTimeProvider();
        var router = new RoutingTranscriber(() => mode, local, cloud, time);

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(30), hotkey, router, new RecordingTextInjector(), () => []);

        var results = new List<DictationResult>();
        engine.Completed += (_, r) => { lock (results) results.Add(r); };

        await SpeakAsync(hotkey, engine);
        await WaitForAsync(() => engine.State == DictationState.Idle, "first dictation");

        // Past the breaker, so the cloud is asked again and answers.
        time.Advance(FallbackTranscriber.CircuitBreakDuration);
        await SpeakAsync(hotkey, engine);
        await WaitForAsync(() => engine.State == DictationState.Idle, "second dictation");

        mode = EngineMode.Parakeet;
        await SpeakAsync(hotkey, engine);
        await WaitForAsync(() => engine.State == DictationState.Idle, "third dictation");

        results.Count.ShouldBe(3);
        (results[0].Engine, results[0].FellBack).ShouldBe((TranscriberKind.Parakeet, true));
        (results[1].Engine, results[1].FellBack).ShouldBe((TranscriberKind.Azure, false));
        (results[2].Engine, results[2].FellBack).ShouldBe((TranscriberKind.Parakeet, false));
    }

    [Fact]
    public async Task A_transcriber_that_does_not_say_is_recorded_as_unknown()
    {
        var hotkey = new FakeHotkeySource();
        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(30), hotkey, new FakeTranscriber("hello"), new RecordingTextInjector(), () => []);

        DictationResult? result = null;
        engine.Completed += (_, r) => result = r;

        await SpeakAsync(hotkey, engine);
        await WaitForAsync(() => engine.State == DictationState.Idle, "done");

        result.ShouldNotBeNull();
        result.Engine.ShouldBe(TranscriberKind.Unknown);
        result.FellBack.ShouldBeFalse();
    }

    /// <summary>An injector whose typing never finishes until it is cancelled.</summary>
    private sealed class HangingInjector : ITextInjector
    {
        public SemaphoreSlim Typing { get; } = new(0);

        public bool ObservedCancellation { get; private set; }

        public int EnterPresses { get; private set; }

        public async ValueTask<bool> InjectAsync(string text, CancellationToken cancellationToken)
        {
            Typing.Release();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }

            return true;
        }

        public ValueTask<bool> PressEnterAsync(CancellationToken cancellationToken)
        {
            EnterPresses++;
            return ValueTask.FromResult(true);
        }
    }
}
