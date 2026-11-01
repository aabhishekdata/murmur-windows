using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>
/// Hold, tap and double tap on the push-to-talk key, against a clock the test drives.
/// </summary>
/// <remarks>
/// The timings are the whole feature, so they are a table: each row is a script of presses (P),
/// releases (R) and milliseconds passing, and the events the engine would see.
/// </remarks>
public sealed class PushToTalkGestureTests
{
    [Theory]
    // Hold: as it always was.
    [InlineData("P 500 R", "Pressed Released")]
    [InlineData("P 300 R", "Pressed Released")]
    // A lone tap is a short hold that arrives a window late: recording carries on until the window
    // closes, then it is released and transcribed like any other.
    [InlineData("P 100 R", "Pressed")]
    [InlineData("P 100 R 299", "Pressed")]
    [InlineData("P 100 R 300", "Pressed Released")]
    [InlineData("P 100 R 400", "Pressed Released")]
    // Double tap latches hands-free; both keys can be let go.
    [InlineData("P 100 R 100 P 50 R", "Pressed HandsFree+")]
    [InlineData("P 299 R 299 P 10 R 5000", "Pressed HandsFree+")]
    [InlineData("P 100 R 100 P 2000 R", "Pressed HandsFree+")]
    // The next tap — or hold — stops it, on the press.
    [InlineData("P 100 R 100 P 50 R 3000 P", "Pressed HandsFree+ HandsFree- Released")]
    [InlineData("P 100 R 100 P 50 R 3000 P 50 R", "Pressed HandsFree+ HandsFree- Released")]
    [InlineData("P 100 R 100 P 50 R 3000 P 900 R", "Pressed HandsFree+ HandsFree- Released")]
    // After stopping, the key works from scratch.
    [InlineData("P 100 R 100 P 50 R 3000 P 50 R 1000 P 600 R", "Pressed HandsFree+ HandsFree- Released Pressed Released")]
    // A second press too late is a new press, after the first tap was released.
    [InlineData("P 100 R 350 P 500 R", "Pressed Released Pressed Released")]
    public void Timing(string script, string expected)
    {
        var (key, gesture, time, log) = Build();
        Run(script, key, time);

        string.Join(' ', log).ShouldBe(expected);
        gesture.Dispose();
    }

    [Fact]
    public void Switched_off_a_tap_is_an_ordinary_short_hold()
    {
        var (key, _, time, log) = Build(enabled: false);

        Run("P 100 R 100 P 50 R", key, time);

        string.Join(' ', log).ShouldBe("Pressed Released Pressed Released");
    }

    [Fact]
    public void Nothing_is_scheduled_at_rest_or_through_a_hold()
    {
        // The idle-cost rule: the double-tap timer is armed only between a tap and the second press.
        var (key, gesture, time, _) = Build();

        gesture.IsAwaitingSecondTap.ShouldBeFalse();
        Run("P 600", key, time);
        gesture.IsAwaitingSecondTap.ShouldBeFalse();
        Run("R", key, time);
        gesture.IsAwaitingSecondTap.ShouldBeFalse();

        Run("P 100 R", key, time);
        gesture.IsAwaitingSecondTap.ShouldBeTrue();
        Run("50 P", key, time);
        gesture.IsAwaitingSecondTap.ShouldBeFalse();
    }

    [Fact]
    public void Reset_forgets_a_latch_and_ignores_the_release_still_to_come()
    {
        var (key, gesture, time, log) = Build();
        Run("P 100 R 100 P", key, time);
        gesture.IsHandsFree.ShouldBeTrue();

        // The engine came to rest — Esc, say — with the second key still down.
        gesture.Reset();
        gesture.IsHandsFree.ShouldBeFalse();
        Run("50 R", key, time);

        string.Join(' ', log).ShouldBe("Pressed HandsFree+ HandsFree-");
    }

    [Fact]
    public void Reset_leaves_a_plain_hold_and_passes_its_release_on()
    {
        // The press may be a new one whose recording is still queued behind the engine's gate:
        // swallowing its release would leave that recording running with no key held.
        var (key, gesture, time, log) = Build();
        Run("P", key, time);

        gesture.Reset();
        Run("100 R", key, time);

        string.Join(' ', log).ShouldBe("Pressed Released", "short, but no longer allowed to become a tap");
        gesture.IsAwaitingSecondTap.ShouldBeFalse();

        Run("50 P 600 R", key, time);
        string.Join(' ', log).ShouldBe("Pressed Released Pressed Released");
    }

    [Fact]
    public void Changing_the_key_forgets_a_hold_too()
    {
        // The old key's release will never be seen, so the new key must start from scratch.
        var (key, gesture, time, log) = Build();
        Run("P", key, time);

        gesture.VirtualKey = 0xA1;
        Run("600 P 600 R", key, time);

        string.Join(' ', log).ShouldBe("Pressed Pressed Released");
    }

    [Fact]
    public void Changing_the_key_forgets_a_tap_in_progress()
    {
        var (key, gesture, time, log) = Build();
        Run("P 100 R", key, time);

        gesture.VirtualKey = 0xA1;
        key.VirtualKey.ShouldBe(0xA1);
        time.Advance(TimeSpan.FromSeconds(1));

        gesture.IsAwaitingSecondTap.ShouldBeFalse();
        string.Join(' ', log).ShouldBe("Pressed");
    }

    [Fact]
    public void Esc_and_its_arming_pass_through_to_the_key()
    {
        var (key, gesture, _, _) = Build();
        var cancels = 0;
        gesture.CancelPressed += (_, _) => cancels++;

        key.PressEscape().ShouldBeFalse("disarmed, Esc is the user's");
        gesture.CancelArmed = true;
        key.CancelArmed.ShouldBeTrue();
        key.PressEscape().ShouldBeTrue();

        cancels.ShouldBe(1);
    }

    internal static (FakeHotkeySource Key, PushToTalkGesture Gesture, ManualTimeProvider Time, List<string> Log) Build(bool enabled = true)
    {
        var key = new FakeHotkeySource();
        var time = new ManualTimeProvider();
        var gesture = new PushToTalkGesture(key, () => enabled, time);
        var log = new List<string>();

        gesture.Pressed += (_, _) => log.Add("Pressed");
        gesture.Released += (_, _) => log.Add("Released");
        gesture.HandsFreeChanged += (_, on) => log.Add(on ? "HandsFree+" : "HandsFree-");

        return (key, gesture, time, log);
    }

    internal static void Run(string script, FakeHotkeySource key, ManualTimeProvider time)
    {
        foreach (var step in script.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (step)
            {
                case "P": key.Press(); break;
                case "R": key.Release(); break;
                default: time.Advance(TimeSpan.FromMilliseconds(int.Parse(step, System.Globalization.CultureInfo.InvariantCulture))); break;
            }
        }
    }
}

/// <summary>Hands-free and Esc, through the real engine.</summary>
public sealed class HandsFreeEngineTests
{
    private static DictationEngine Build(IHotkeySource hotkey, ITranscriber transcriber, RecordingTextInjector injector, IAudioCapture? capture = null) =>
        new(capture ?? FakeAudioCapture.Tone(1.0), hotkey, transcriber, injector, () => [], new FakeClock());

    [Fact]
    public async Task A_double_tap_keeps_recording_until_the_next_tap()
    {
        var (key, gesture, time, _) = PushToTalkGestureTests.Build();
        var injector = new RecordingTextInjector();
        await using var engine = Build(gesture, new FakeTranscriber("hands free works"), injector);

        PushToTalkGestureTests.Run("P 100 R 100 P 50 R", key, time);
        await DictationEngineTests.UntilAsync(() => engine.Level > 0);
        engine.State.ShouldBe(DictationState.Recording);
        gesture.IsHandsFree.ShouldBeTrue();

        // Long after any window has closed, it is still listening.
        time.Advance(TimeSpan.FromSeconds(10));
        engine.State.ShouldBe(DictationState.Recording);

        PushToTalkGestureTests.Run("P", key, time);
        await DictationEngineTests.UntilAsync(() => engine.State == DictationState.Idle);

        injector.Injected.ShouldBe(["hands free works"]);
        gesture.IsHandsFree.ShouldBeFalse();
    }

    [Fact]
    public async Task A_lone_tap_is_transcribed_once_the_window_closes()
    {
        // A one-word "yes" said on a quick press is still a dictation.
        var key = new FakeHotkeySource();
        var time = new ManualTimeProvider();
        var gesture = new PushToTalkGesture(key, time: time);
        var transcriber = new FakeTranscriber("yes");
        var injector = new RecordingTextInjector();
        await using var engine = Build(gesture, transcriber, injector);

        PushToTalkGestureTests.Run("P 100 R", key, time);
        await DictationEngineTests.UntilAsync(() => engine.Level > 0);
        engine.State.ShouldBe(DictationState.Recording, "still listening for the second tap");

        time.Advance(PushToTalkGesture.DoubleTapWindow);
        await DictationEngineTests.UntilAsync(() => engine.State == DictationState.Idle && injector.Injected.Count > 0);

        injector.Injected.ShouldBe(["yes"]);
        gesture.IsHandsFree.ShouldBeFalse();
    }

    [Fact]
    public async Task Esc_once_the_text_is_ready_reaches_the_app_and_cancels_nothing()
    {
        // Delivery — history, the paste, Enter — is never cut short: cancelling a paste in flight
        // would put the old clipboard back before the app had read it.
        var key = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(key, new FakeTranscriber("delivered"), injector);

        bool? armed = null;
        bool? swallowed = null;
        engine.Completed += (_, _) =>
        {
            armed = key.CancelArmed;
            swallowed = key.PressEscape();
            _ = engine.CancelAsync(includeTranscription: true);
        };

        await DictationEngineTests.DictateAsync(key, engine);
        await DictationEngineTests.UntilAsync(() => injector.Injected.Count > 0);

        armed.ShouldBe(false);
        swallowed.ShouldBe(false, "the Esc belongs to the app the text went to");
        injector.Injected.ShouldBe(["delivered"]);

        // And nothing is left behind for the next dictation.
        await DictationEngineTests.DictateAsync(key, engine);
        await DictationEngineTests.UntilAsync(() => injector.Injected.Count > 1);
        injector.Injected.ShouldBe(["delivered", "delivered"]);
    }

    [Fact]
    public async Task Esc_after_a_hold_was_cancelled_leaves_the_key_working()
    {
        // Esc mid-hold brings the engine to rest with the key still down. The release that follows
        // is passed on and ignored; it must not open a double-tap window that could latch.
        var key = new FakeHotkeySource();
        var time = new ManualTimeProvider();
        var gesture = new PushToTalkGesture(key, time: time);
        var injector = new RecordingTextInjector();
        await using var engine = Build(gesture, new FakeTranscriber("after"), injector);

        PushToTalkGestureTests.Run("P", key, time);
        await DictationEngineTests.UntilAsync(() => engine.Level > 0);
        key.PressEscape().ShouldBeTrue();
        await DictationEngineTests.UntilAsync(() => engine.State == DictationState.Idle);

        PushToTalkGestureTests.Run("100 R", key, time);
        await DictationEngineTests.UntilAsync(() => !gesture.IsAwaitingSecondTap);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        engine.State.ShouldBe(DictationState.Idle);
        injector.Injected.ShouldBeEmpty();

        PushToTalkGestureTests.Run("50 P", key, time);
        await DictationEngineTests.UntilAsync(() => engine.Level > 0);
        PushToTalkGestureTests.Run("600 R", key, time);
        await DictationEngineTests.UntilAsync(() => engine.State == DictationState.Idle && injector.Injected.Count > 0);
        injector.Injected.ShouldBe(["after"]);
    }

    [Fact]
    public async Task Esc_is_armed_only_while_there_is_a_dictation_to_cancel()
    {
        var key = new FakeHotkeySource();
        var transcriber = new ScriptedTranscriber().Hangs();
        await using var engine = Build(key, transcriber, new RecordingTextInjector());

        key.CancelArmed.ShouldBeFalse("at rest Esc belongs to the user");
        key.PressEscape().ShouldBeFalse();

        key.Press();
        await DictationEngineTests.UntilAsync(() => engine.State == DictationState.Recording);
        key.CancelArmed.ShouldBeTrue("recording");

        key.Release();
        (await transcriber.WaitForCallAsync()).ShouldBeTrue();
        engine.State.ShouldBe(DictationState.Transcribing);
        key.CancelArmed.ShouldBeTrue("transcribing");

        key.PressEscape().ShouldBeTrue();
        await DictationEngineTests.UntilAsync(() => engine.State == DictationState.Idle);
        key.CancelArmed.ShouldBeFalse("back at rest");
    }

    [Fact]
    public async Task Esc_while_recording_discards_the_audio_and_types_nothing()
    {
        var key = new FakeHotkeySource();
        var transcriber = new FakeTranscriber("never typed");
        var injector = new RecordingTextInjector();
        await using var engine = Build(key, transcriber, injector);

        var completed = 0;
        engine.Completed += (_, _) => completed++;

        key.Press();
        await DictationEngineTests.UntilAsync(() => engine.Level > 0);
        key.PressEscape().ShouldBeTrue();
        await DictationEngineTests.UntilAsync(() => engine.State == DictationState.Idle);

        // The release that follows finds the engine at rest and does nothing.
        key.Release();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        engine.State.ShouldBe(DictationState.Idle);
        transcriber.SegmentLengths.ShouldBeEmpty();
        injector.Injected.ShouldBeEmpty();
        completed.ShouldBe(0);
    }

    [Fact]
    public async Task Esc_while_transcribing_abandons_it_without_a_fault_or_a_fallback()
    {
        var key = new FakeHotkeySource();
        var cloud = new ScriptedTranscriber(TranscriberKind.Azure).Hangs();
        var offline = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("the fallback must not answer");
        var router = new RoutingTranscriber(() => EngineMode.AzureWithParakeetFallback, offline, cloud);
        var injector = new RecordingTextInjector();
        await using var engine = Build(key, router, injector);

        var faults = 0;
        var completed = 0;
        engine.TranscriptionFaulted += (_, _) => faults++;
        engine.Completed += (_, _) => completed++;

        key.Press();
        await DictationEngineTests.UntilAsync(() => engine.Level > 0);
        key.Release();
        (await cloud.WaitForCallAsync()).ShouldBeTrue();

        key.PressEscape().ShouldBeTrue();
        await DictationEngineTests.UntilAsync(() => engine.State == DictationState.Idle);

        engine.State.ShouldBe(DictationState.Idle);
        await DictationEngineTests.UntilAsync(() => cloud.ObservedCancellation);
        cloud.ObservedCancellation.ShouldBeTrue();
        offline.Calls.ShouldBe(0, "a cancelled dictation is never retried anywhere");
        injector.Injected.ShouldBeEmpty();
        faults.ShouldBe(0);
        completed.ShouldBe(0);
    }

    [Fact]
    public async Task Esc_in_hands_free_cancels_and_ends_the_latch()
    {
        var key = new FakeHotkeySource();
        var time = new ManualTimeProvider();
        var gesture = new PushToTalkGesture(key, time: time);
        var injector = new RecordingTextInjector();
        await using var engine = Build(gesture, new FakeTranscriber("never typed"), injector);

        PushToTalkGestureTests.Run("P 100 R 100 P 50 R", key, time);
        await DictationEngineTests.UntilAsync(() => engine.Level > 0);
        gesture.IsHandsFree.ShouldBeTrue();

        key.PressEscape().ShouldBeTrue();
        await DictationEngineTests.UntilAsync(() => engine.State == DictationState.Idle);

        gesture.IsHandsFree.ShouldBeFalse();
        injector.Injected.ShouldBeEmpty();

        // And the key starts from scratch: a hold dictates normally.
        PushToTalkGestureTests.Run("P", key, time);
        await DictationEngineTests.UntilAsync(() => engine.Level > 0);
        PushToTalkGestureTests.Run("600 R", key, time);
        await DictationEngineTests.UntilAsync(() => engine.State == DictationState.Idle && injector.Injected.Count > 0);
        injector.Injected.ShouldBe(["never typed"]);
    }

    [Fact]
    public async Task A_finished_dictation_hands_its_audio_and_id_on()
    {
        var key = new FakeHotkeySource();
        await using var engine = Build(key, new FakeTranscriber("kept"), new RecordingTextInjector());

        DictationResult? result = null;
        engine.Completed += (_, r) => result = r;
        await DictationEngineTests.DictateAsync(key, engine);

        result.ShouldNotBeNull();
        result.Id.ShouldNotBe(Guid.Empty);
        result.Audio.Length.ShouldBeGreaterThan(0, "the samples that were transcribed");
    }
}
