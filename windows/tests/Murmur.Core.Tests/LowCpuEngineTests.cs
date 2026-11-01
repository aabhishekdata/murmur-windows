using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>
/// The engine changes that let the UI stop polling, and the settings it now honours.
/// </summary>
public sealed class EngineEventAndSettingTests
{
    private static Task WaitForAsync(Func<bool> condition) => DictationEngineTests.UntilAsync(condition);

    /// <summary>Presses, waits for audio to arrive, then releases and waits for idle.</summary>
    private static async Task DictateAsync(FakeHotkeySource hotkey, DictationEngine engine)
    {
        hotkey.Press();
        await WaitForAsync(() => engine.State == DictationState.Recording);
        await WaitForAsync(() => engine.Level > 0);

        hotkey.Release();
        await WaitForAsync(() => engine.State == DictationState.Idle);
    }

    [Fact]
    public async Task StateChanged_fires_once_per_transition_and_never_for_level_updates()
    {
        // This event replaced a 60 ms timer, so it must stay quiet: the bar is driven by it,
        // and a flood here would just be the poll again by another name.
        var hotkey = new FakeHotkeySource();
        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(0.5), hotkey, new FakeTranscriber("hello"),
            new RecordingTextInjector(), () => []);

        var states = new List<DictationState>();
        var changes = 0;
        engine.StateChanged += (_, state) => { lock (states) states.Add(state); };
        engine.Changed += (_, _) => Interlocked.Increment(ref changes);

        await DictateAsync(hotkey, engine);

        states.ShouldBe([DictationState.Recording, DictationState.Transcribing, DictationState.Idle]);
        changes.ShouldBeGreaterThan(states.Count, "Changed still reports every level update");
    }

    [Fact]
    public async Task InjectText_off_keeps_the_dictation_but_types_nothing()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        var inject = false;

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(0.4), hotkey, new FakeTranscriber("Send this. Press enter."),
            injector, () => [], injectText: () => inject);

        DictationResult? completed = null;
        engine.Completed += (_, r) => completed = r;

        await DictateAsync(hotkey, engine);

        completed.ShouldNotBeNull("history still receives the dictation");
        completed.Text.ShouldBe("Send this.");
        injector.Injected.ShouldBeEmpty("the setting says not to type into the focused app");
        injector.EnterPresses.ShouldBe(0, "a spoken press enter is a keystroke too");

        // Read per utterance: switching it back on applies to the very next dictation.
        inject = true;
        await DictateAsync(hotkey, engine);
        injector.Injected.ShouldBe(["Send this."]);
        injector.EnterPresses.ShouldBe(1);
    }

    [Fact]
    public async Task A_bare_press_enter_is_withheld_when_injection_is_off()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(0.4), hotkey, new FakeTranscriber("Press enter."),
            injector, () => [], injectText: static () => false);

        await DictateAsync(hotkey, engine);

        injector.EnterPresses.ShouldBe(0);
    }

    [Fact]
    public async Task Changing_the_key_while_idle_takes_effect_immediately()
    {
        var hotkey = new FakeHotkeySource();
        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(0.2), hotkey, new FakeTranscriber("x"),
            new RecordingTextInjector(), () => []);

        await engine.SetPushToTalkKeyAsync(0x7C);

        hotkey.VirtualKey.ShouldBe(0x7C);
        engine.State.ShouldBe(DictationState.Idle);
    }

    [Fact]
    public async Task Changing_the_key_mid_recording_finishes_that_recording_first()
    {
        // Otherwise the engine waits forever for the release of a key it no longer watches.
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(2.0), hotkey, new FakeTranscriber("what I had said"),
            injector, () => []);

        hotkey.Press();
        await WaitForAsync(() => engine.Level > 0);
        engine.State.ShouldBe(DictationState.Recording);

        await engine.SetPushToTalkKeyAsync(0xA1);

        hotkey.VirtualKey.ShouldBe(0xA1);
        engine.State.ShouldBe(DictationState.Idle);
        injector.Injected.ShouldBe(["what I had said"], "what was already said is not thrown away");

        // The old key's release arriving late is harmless.
        hotkey.Release();
        engine.State.ShouldBe(DictationState.Idle);
    }
}

/// <summary>The pooled sample buffer behind each recording.</summary>
public sealed class SampleBufferTests
{
    [Fact]
    public void Appends_survive_growth_and_come_back_exactly()
    {
        using var buffer = new SampleBuffer();
        var expected = new List<float>();

        // Ten seconds in 20 ms chunks: well past the initial capacity, so it grows repeatedly.
        for (var chunk = 0; chunk < 500; chunk++)
        {
            var samples = Enumerable.Range(0, 320).Select(i => (float)((chunk * 320) + i)).ToArray();
            buffer.Append(samples);
            expected.AddRange(samples);
        }

        buffer.Count.ShouldBe(expected.Count);
        buffer.ToArray().ShouldBe(expected.ToArray());
        buffer.Memory.Length.ShouldBe(expected.Count);
    }

    [Fact]
    public void ToArray_is_exact_length_so_a_recogniser_needs_no_second_copy()
    {
        using var buffer = new SampleBuffer();
        buffer.Append([0.1f, 0.2f, 0.3f]);

        buffer.ToArray().Length.ShouldBe(3);
    }

    [Fact]
    public void An_empty_buffer_yields_an_empty_array()
    {
        using var buffer = new SampleBuffer();
        buffer.ToArray().ShouldBeEmpty();
    }

    [Fact]
    public void A_disposed_buffer_refuses_appends_rather_than_writing_into_a_returned_array()
    {
        var buffer = new SampleBuffer();
        buffer.Dispose();

        Should.Throw<ObjectDisposedException>(() => buffer.Append([1f]));
        buffer.Dispose();
    }
}

/// <summary>Debounced, atomic settings writes.</summary>
public sealed class SettingsSaveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"murmur-save-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _time = new();

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static int KeyOnDisk(string path) => new AppSettings(path).Data.PushToTalkKey;

    [Fact]
    public void Nothing_is_written_until_updates_pause()
    {
        using var settings = new AppSettings(SettingsPath, _time);

        settings.Update(settings.Data with { PushToTalkKey = 0x7C });
        File.Exists(SettingsPath).ShouldBeFalse("the write waits for the debounce");
        settings.HasPendingSave.ShouldBeTrue();

        _time.Advance(AppSettings.SaveDelay - TimeSpan.FromMilliseconds(1));
        File.Exists(SettingsPath).ShouldBeFalse();

        _time.Advance(TimeSpan.FromMilliseconds(1));
        KeyOnDisk(SettingsPath).ShouldBe(0x7C);
        settings.HasPendingSave.ShouldBeFalse();
    }

    [Fact]
    public void A_burst_of_updates_writes_once_with_the_last_value()
    {
        using var settings = new AppSettings(SettingsPath, _time);

        // Dragging the bar: a stream of updates, each inside the previous one's delay.
        for (var i = 0; i < 10; i++)
        {
            settings.Update(settings.Data with { FlowBarOffset = i / 10.0 });
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }

        File.Exists(SettingsPath).ShouldBeFalse("each update restarts the wait");

        _time.Advance(AppSettings.SaveDelay);
        new AppSettings(SettingsPath).Data.FlowBarOffset.ShouldBe(0.9);
    }

    [Fact]
    public void Changed_and_Data_update_at_once_even_though_the_file_waits()
    {
        using var settings = new AppSettings(SettingsPath, _time);
        var raised = 0;
        settings.Changed += (_, _) => raised++;

        settings.Update(settings.Data with { InjectText = false });

        raised.ShouldBe(1);
        settings.Data.InjectText.ShouldBeFalse();
    }

    [Fact]
    public void Flush_writes_a_pending_change_now()
    {
        var settings = new AppSettings(SettingsPath, _time);
        settings.Update(settings.Data with { PushToTalkKey = 0xA1 });

        settings.Flush();

        KeyOnDisk(SettingsPath).ShouldBe(0xA1);
        settings.HasPendingSave.ShouldBeFalse();
        settings.Dispose();
    }

    [Fact]
    public void Dispose_flushes_so_quitting_never_loses_the_last_change()
    {
        var settings = new AppSettings(SettingsPath, _time);
        settings.Update(settings.Data with { PushToTalkKey = 0x14 });

        settings.Dispose();

        KeyOnDisk(SettingsPath).ShouldBe(0x14);
    }

    [Fact]
    public void The_write_replaces_the_file_whole_and_leaves_no_temporary_behind()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "PushToTalkKey": 161 }""");

        using var settings = new AppSettings(SettingsPath, _time);
        settings.Data.PushToTalkKey.ShouldBe(0xA1);

        settings.Update(settings.Data with { PushToTalkKey = 0x7C });
        settings.Flush();

        KeyOnDisk(SettingsPath).ShouldBe(0x7C);
        Directory.GetFiles(_directory).ShouldBe([SettingsPath], "only settings.json remains");
    }

    [Fact]
    public void A_failed_write_is_reported_and_retried_rather_than_thrown()
    {
        // A directory where the file should be: every write fails, which stands in for a file
        // locked by an editor or a backup agent.
        Directory.CreateDirectory(SettingsPath);

        using var settings = new AppSettings(SettingsPath, _time);
        var failures = new List<Exception>();
        settings.SaveFailed += (_, ex) => failures.Add(ex);

        settings.Update(settings.Data with { PushToTalkKey = 0x7C });
        Should.NotThrow(settings.Flush);

        failures.ShouldHaveSingleItem();
        settings.HasPendingSave.ShouldBeTrue("the change is kept for the next attempt");
        settings.Data.PushToTalkKey.ShouldBe(0x7C, "the values in memory are unaffected");

        Directory.Delete(SettingsPath);
        settings.Flush();
        KeyOnDisk(SettingsPath).ShouldBe(0x7C);
    }

    [Fact]
    public void SaveFailed_is_raised_outside_the_lock_so_the_UI_thread_can_still_update()
    {
        Directory.CreateDirectory(SettingsPath);

        using var settings = new AppSettings(SettingsPath, _time);
        var updatedFromAnotherThread = false;

        // The handler logs to disk in the app. If it ran under the lock, an Update on the UI
        // thread would wait for it — this one, on another thread, would time out.
        settings.SaveFailed += (_, _) =>
            updatedFromAnotherThread = Task.Run(() => settings.Update(settings.Data with { InjectText = false }))
                .Wait(TimeSpan.FromSeconds(5));

        settings.Update(settings.Data with { PushToTalkKey = 0x7C });
        settings.Flush();

        updatedFromAnotherThread.ShouldBeTrue();
        settings.Data.InjectText.ShouldBeFalse();
        settings.HasPendingSave.ShouldBeTrue();
        Directory.Delete(SettingsPath);
    }

    [Fact]
    public void An_update_after_a_flush_is_kept_pending_and_written_by_its_own_timer()
    {
        using var settings = new AppSettings(SettingsPath, _time);
        settings.Update(settings.Data with { PushToTalkKey = 0x7C });

        settings.Flush();
        settings.Update(settings.Data with { PushToTalkKey = 0x14 });

        settings.HasPendingSave.ShouldBeTrue("the newer value has not been written");
        KeyOnDisk(SettingsPath).ShouldBe(0x7C);

        _time.Advance(AppSettings.SaveDelay);
        KeyOnDisk(SettingsPath).ShouldBe(0x14);
        settings.HasPendingSave.ShouldBeFalse();
    }

    [Fact]
    public void A_flush_with_nothing_pending_does_not_touch_the_disk()
    {
        using var settings = new AppSettings(SettingsPath, _time);

        settings.Flush();

        File.Exists(SettingsPath).ShouldBeFalse();
    }
}
