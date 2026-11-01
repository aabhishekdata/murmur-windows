using System.Text.Json;
using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Dictionary;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>Paste the last transcript again, from a global shortcut.</summary>
public sealed class PasteLastTranscriptTests
{
    private static readonly HotkeyChord CtrlAltV = HotkeyChords.Parse("Ctrl+Alt+V");

    [Fact]
    public async Task The_shortcut_pastes_the_last_transcript()
    {
        var hotkeys = new FakeGlobalHotkeys();
        var injector = new RecordingTextInjector();
        using var paste = new PasteLastTranscript(hotkeys, injector);

        paste.Bind(CtrlAltV).ShouldBe(SnippetHotkeyStatus.Active);
        hotkeys.Registered[PasteLastTranscript.HotkeyId].ShouldBe(CtrlAltV);

        paste.Remember("first");
        paste.Remember("second");
        hotkeys.Press(CtrlAltV).ShouldBeTrue();
        await paste.PasteAsync(string.Empty);
        await DictationEngineTests.UntilAsync(() => injector.Injected.Count > 0);

        injector.Injected.ShouldBe(["second"]);
    }

    [Fact]
    public async Task With_nothing_dictated_yet_the_shortcut_does_nothing()
    {
        var injector = new RecordingTextInjector();
        using var paste = new PasteLastTranscript(new FakeGlobalHotkeys(), injector);

        (await paste.PasteAsync()).ShouldBeFalse();
        paste.Remember("   ");
        paste.Text.ShouldBeNull("blank text is not a transcript");
        injector.Injected.ShouldBeEmpty();
    }

    [Fact]
    public async Task Pressed_mid_recording_it_cancels_the_dictation_first()
    {
        var order = new List<string>();
        var injector = new RecordingTextInjector();
        using var paste = new PasteLastTranscript(null, injector, () =>
        {
            order.Add("cancel");
            return Task.CompletedTask;
        });

        paste.Remember("again");
        (await paste.PasteAsync()).ShouldBeTrue();
        order.ShouldBe(["cancel"]);
        injector.Injected.ShouldBe(["again"]);
    }

    [Fact]
    public void A_combination_someone_else_holds_is_reported_not_hidden()
    {
        var hotkeys = new FakeGlobalHotkeys();
        hotkeys.Taken.Add(CtrlAltV);
        using var paste = new PasteLastTranscript(hotkeys, new RecordingTextInjector());

        paste.Bind(CtrlAltV).ShouldBe(SnippetHotkeyStatus.InUse);
        paste.Bind(new HotkeyChord(HotkeyModifiers.None, 'V')).ShouldBe(SnippetHotkeyStatus.Invalid);
        paste.Bind(null).ShouldBe(SnippetHotkeyStatus.None);
        hotkeys.Registered.ShouldBeEmpty();

        using var nowhere = new PasteLastTranscript(null, new RecordingTextInjector());
        nowhere.Bind(CtrlAltV).ShouldBe(SnippetHotkeyStatus.Unsupported);
    }

    [Fact]
    public void Rebinding_gives_the_old_combination_back()
    {
        var hotkeys = new FakeGlobalHotkeys();
        using var paste = new PasteLastTranscript(hotkeys, new RecordingTextInjector());
        var other = HotkeyChords.Parse("Ctrl+Shift+V");

        paste.Bind(CtrlAltV);
        paste.Bind(other).ShouldBe(SnippetHotkeyStatus.Active);

        hotkeys.Registered.ShouldHaveSingleItem().Value.ShouldBe(other);
    }

    [Fact]
    public void Suspended_while_a_new_shortcut_is_recorded_then_whatever_was_chosen_is_registered()
    {
        var hotkeys = new FakeGlobalHotkeys();
        using var paste = new PasteLastTranscript(hotkeys, new RecordingTextInjector());
        paste.Bind(CtrlAltV);

        var suspension = paste.Suspend();
        hotkeys.Registered.ShouldBeEmpty("Windows would swallow it before the recorder saw it");

        // The user records "none" while suspended; resuming must not bring the old one back.
        paste.Bind(null);
        suspension.Dispose();
        hotkeys.Registered.ShouldBeEmpty();

        using (paste.Suspend()) paste.Bind(CtrlAltV);
        hotkeys.Registered[PasteLastTranscript.HotkeyId].ShouldBe(CtrlAltV);
    }

    [Fact]
    public async Task A_prompt_shortcut_does_not_paste_the_transcript()
    {
        var hotkeys = new FakeGlobalHotkeys();
        var injector = new RecordingTextInjector();
        using var paste = new PasteLastTranscript(hotkeys, injector);
        paste.Remember("transcript");
        paste.Bind(CtrlAltV);

        var prompt = HotkeyChords.Parse("Ctrl+Alt+1");
        hotkeys.Register(1, prompt);
        hotkeys.Press(prompt);
        await Task.Delay(20, TestContext.Current.CancellationToken);

        injector.Injected.ShouldBeEmpty();
    }

    [Fact]
    public void Nothing_is_taken_from_other_apps_until_the_user_picks_a_shortcut()
    {
        new SettingsData().PasteLastHotkey.ShouldBeNull("Ctrl+Alt+V is Paste Special in Office");
    }

    [Fact]
    public void A_clash_with_a_saved_prompt_is_reported_as_murmurs_own_both_ways()
    {
        var directory = Path.Combine(Path.GetTempPath(), "murmur-clash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var hotkeys = new FakeGlobalHotkeys();
            var store = new SnippetStore(Path.Combine(directory, "snippets.jsonl"));
            var prompt = HotkeyChords.Parse("Ctrl+Alt+1");
            store.Add(new SnippetEntry { Trigger = "review prompt", Text = "Review this.", Hotkey = prompt });

            using var binder = new SnippetHotkeyBinder(store, hotkeys, new RecordingTextInjector());
            using var paste = new PasteLastTranscript(
                hotkeys, new RecordingTextInjector(), heldByPrompt: c => store.Entries.Any(e => e.Hotkey == c));
            binder.PasteLastChord = () => paste.Chord;

            // Paste-last asked for the prompt's combination: the prompt keeps it, and Settings
            // says a prompt has it rather than "another app".
            paste.Bind(prompt).ShouldBe(SnippetHotkeyStatus.Duplicate);
            SnippetHotkeyBinder.Describe(SnippetHotkeyStatus.Duplicate).ShouldNotBeNull().ShouldContain("prompt");

            // A prompt asking for paste-last's combination is told it is Murmur's own.
            paste.Bind(CtrlAltV).ShouldBe(SnippetHotkeyStatus.Active);
            binder.Check(CtrlAltV, Guid.NewGuid()).ShouldBe(SnippetHotkeyStatus.PasteLast);
            SnippetHotkeyBinder.Describe(SnippetHotkeyStatus.PasteLast).ShouldNotBeNull().ShouldContain("paste-last");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Recording_a_shortcut_gives_back_every_combination_murmur_holds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "murmur-suspend", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var hotkeys = new FakeGlobalHotkeys();
            var store = new SnippetStore(Path.Combine(directory, "snippets.jsonl"));
            var prompt = HotkeyChords.Parse("Ctrl+Alt+1");
            store.Add(new SnippetEntry { Trigger = "review prompt", Text = "Review this.", Hotkey = prompt });

            using var binder = new SnippetHotkeyBinder(store, hotkeys, new RecordingTextInjector());
            using var paste = new PasteLastTranscript(hotkeys, new RecordingTextInjector());
            paste.Bind(CtrlAltV);
            hotkeys.Registered.Count.ShouldBe(2);

            using (ShortcutSuspension.All(binder, null, paste))
            {
                hotkeys.Registered.ShouldBeEmpty("Windows would swallow either before the recorder saw it");
                hotkeys.Press(prompt).ShouldBeFalse();
                hotkeys.Press(CtrlAltV).ShouldBeFalse();
            }

            hotkeys.Registered.Values.ShouldBe([prompt, CtrlAltV], ignoreOrder: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Disposing_unregisters()
    {
        var hotkeys = new FakeGlobalHotkeys();
        var paste = new PasteLastTranscript(hotkeys, new RecordingTextInjector());
        paste.Bind(CtrlAltV);

        paste.Dispose();
        hotkeys.Registered.ShouldBeEmpty();
    }
}

/// <summary>The last few dictations' audio, in memory.</summary>
public sealed class RecentAudioTests
{
    private static ReadOnlyMemory<float> Seconds(double seconds) => new float[(int)(seconds * AudioChunk.SampleRate)];

    [Fact]
    public void Keeps_the_newest_five()
    {
        var audio = new RecentAudio();
        var ids = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToList();

        foreach (var id in ids) audio.Add(id, Seconds(1));

        audio.Count.ShouldBe(RecentAudio.Capacity);
        audio.Contains(ids[0]).ShouldBeFalse();
        audio.Contains(ids[1]).ShouldBeFalse();
        audio.Contains(ids[6]).ShouldBeTrue();
        audio.Contains(ids[2]).ShouldBeTrue();
    }

    [Fact]
    public void Never_holds_more_than_ten_minutes_between_them()
    {
        var audio = new RecentAudio();
        var old = Guid.NewGuid();
        var recent = Guid.NewGuid();

        audio.Add(old, Seconds(240));
        audio.Add(Guid.NewGuid(), Seconds(240));
        audio.Add(recent, Seconds(240));

        audio.Contains(old).ShouldBeFalse("twelve minutes is over the cap, so the oldest goes");
        audio.Contains(recent).ShouldBeTrue();

        var huge = Guid.NewGuid();
        audio.Add(huge, Seconds(11 * 60));
        audio.Contains(huge).ShouldBeFalse("one utterance over the cap on its own is not kept");
    }

    [Fact]
    public void Rename_remove_and_clear()
    {
        var audio = new RecentAudio();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        audio.Add(a, Seconds(1));

        audio.Rename(a, b);
        audio.Contains(a).ShouldBeFalse();
        audio.TryGet(b, out var samples).ShouldBeTrue();
        samples.Length.ShouldBe(AudioChunk.SampleRate);

        audio.Remove(b);
        audio.Count.ShouldBe(0);

        audio.Add(a, Seconds(1));
        audio.Clear();
        audio.Count.ShouldBe(0);
        audio.Add(a, ReadOnlyMemory<float>.Empty);
        audio.Count.ShouldBe(0, "nothing to keep");
    }
}

/// <summary>The opt-in that keeps recent audio on disk for a day.</summary>
public sealed class RecentAudioOnDiskTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "murmur-audio", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private bool _keep = true;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { /* never written */ }
        catch (IOException) { /* best effort */ }
    }

    private RecentAudio Open() => new(_directory, () => _keep, _time);

    private static float[] Tone(int length) => Enumerable.Range(0, length).Select(i => i / 1000f).ToArray();

    private int Files => Directory.Exists(_directory) ? Directory.GetFiles(_directory).Length : 0;

    [Fact]
    public void Off_nothing_is_written()
    {
        _keep = false;
        using var audio = Open();

        audio.Add(Guid.NewGuid(), Tone(1600));

        Directory.Exists(_directory).ShouldBeFalse("audio stays in memory unless the user opts in");
        audio.Count.ShouldBe(1);
    }

    [Fact]
    public void On_the_audio_survives_a_restart_and_memory_eviction()
    {
        var first = Guid.NewGuid();
        var samples = Tone(1600);
        using (var audio = Open())
        {
            audio.Add(first, samples);
            for (var i = 0; i < RecentAudio.Capacity; i++) audio.Add(Guid.NewGuid(), Tone(160));

            audio.Count.ShouldBe(RecentAudio.Capacity);
            audio.Contains(first).ShouldBeTrue("pushed out of memory, still on disk");
        }

        using var restarted = Open();
        restarted.Contains(first).ShouldBeTrue();
        restarted.TryGet(first, out var read).ShouldBeTrue();
        read.ToArray().ShouldBe(samples);
    }

    [Fact]
    public void A_file_is_gone_after_24_hours()
    {
        using var audio = Open();
        var id = Guid.NewGuid();
        audio.Add(id, Tone(1600));

        _time.Advance(RecentAudio.DiskLifetime - TimeSpan.FromMinutes(1));
        Files.ShouldBe(1);

        // The timer the write armed fires at expiry and deletes it.
        _time.Advance(TimeSpan.FromMinutes(1));
        Files.ShouldBe(0);

        using var restarted = Open();
        restarted.Contains(id).ShouldBeFalse();
    }

    [Fact]
    public void An_expired_file_is_never_read_back_even_before_the_purge()
    {
        var id = Guid.NewGuid();
        using (var audio = Open()) audio.Add(id, Tone(1600));

        // A new instance has not purged yet; the file is there but past its day.
        _time.Advance(RecentAudio.DiskLifetime);
        using var restarted = Open();
        restarted.Contains(id).ShouldBeFalse();
        restarted.TryGet(id, out _).ShouldBeFalse();

        restarted.Purge();
        Files.ShouldBe(0);
    }

    [Fact]
    public void Delete_delete_all_rename_and_switching_off()
    {
        using var audio = Open();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        audio.Add(a, Tone(1600));
        audio.Add(b, Tone(1600));
        Files.ShouldBe(2);

        audio.Rename(a, c);
        File.Exists(Path.Combine(_directory, c.ToString("N") + ".f32")).ShouldBeTrue();
        Files.ShouldBe(2);

        audio.Remove(c);
        Files.ShouldBe(1);

        audio.Clear();
        Files.ShouldBe(0);
        audio.Count.ShouldBe(0);

        audio.Add(a, Tone(1600));
        _keep = false;
        audio.Purge();
        Files.ShouldBe(0, "with the setting off, a start-up purge deletes everything");
        audio.Contains(a).ShouldBeTrue("memory is untouched");
    }

    [Fact]
    public void A_disk_failure_is_reported_and_the_memory_copy_stands()
    {
        // A file where the directory should be: every write fails.
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        File.WriteAllText(_directory, "not a directory");
        try
        {
            using var audio = Open();
            var failures = 0;
            audio.DiskFailed += (_, _) => failures++;

            var id = Guid.NewGuid();
            audio.Add(id, Tone(1600));

            failures.ShouldBeGreaterThan(0);
            audio.Contains(id).ShouldBeTrue();
        }
        finally
        {
            File.Delete(_directory);
        }
    }

    [Fact]
    public void A_write_that_fails_leaves_no_half_written_file_behind()
    {
        // A directory where the finished file should go: the rename after the write fails.
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(_directory, id.ToString("N") + ".f32"));
        using var audio = Open();
        var failures = 0;
        audio.DiskFailed += (_, _) => failures++;

        audio.Add(id, Tone(1600));

        failures.ShouldBeGreaterThan(0);
        Directory.GetFiles(_directory).ShouldBeEmpty("the raw audio must not outlive a failed write");
        audio.Contains(id).ShouldBeTrue("the memory copy stands");
    }

    [Fact]
    public void A_file_left_half_written_by_a_crash_is_deleted()
    {
        Directory.CreateDirectory(_directory);
        var leftover = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".f32.tmp");
        File.WriteAllBytes(leftover, new byte[64]);
        File.SetLastWriteTimeUtc(leftover, _time.GetUtcNow().UtcDateTime);

        // Young enough to be a write in progress: kept, and the timer armed for it.
        using (var audio = Open())
        {
            audio.Purge();
            File.Exists(leftover).ShouldBeTrue();

            _time.Advance(RecentAudio.TemporaryGrace);
            File.Exists(leftover).ShouldBeFalse("the purge timer removes it once its grace is over");
        }

        // Switching the setting off, and "Delete all", remove one at once.
        File.WriteAllBytes(leftover, new byte[64]);
        File.SetLastWriteTimeUtc(leftover, _time.GetUtcNow().UtcDateTime);
        using (var audio = Open())
        {
            audio.ForgetOnDisk();
            File.Exists(leftover).ShouldBeFalse();
        }

        File.WriteAllBytes(leftover, new byte[64]);
        using (var audio = Open())
        {
            audio.Clear();
            File.Exists(leftover).ShouldBeFalse();
        }
    }

    [Fact]
    public void The_setting_is_off_by_default_and_round_trips()
    {
        new SettingsData().KeepRecentAudioOnDisk.ShouldBeFalse();

        var json = JsonSerializer.Serialize(new SettingsData { KeepRecentAudioOnDisk = true }, SettingsJsonContext.Default.SettingsData);
        JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsData)!.KeepRecentAudioOnDisk.ShouldBeTrue();
    }
}

/// <summary>History's "Retry with the other engine".</summary>
public sealed class TranscriptRetryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "murmur-retry", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Theory]
    [InlineData(TranscriberKind.Parakeet, EngineMode.Parakeet, true, true, true)]
    [InlineData(TranscriberKind.Parakeet, EngineMode.Azure, true, true, true)]
    [InlineData(TranscriberKind.Parakeet, EngineMode.Parakeet, false, true, false)]
    // Audio goes to Azure only while an Azure engine is chosen — even for a retry.
    [InlineData(TranscriberKind.Azure, EngineMode.Parakeet, true, true, false)]
    [InlineData(TranscriberKind.Azure, EngineMode.Azure, true, true, true)]
    [InlineData(TranscriberKind.Azure, EngineMode.AzureWithParakeetFallback, true, true, true)]
    [InlineData(TranscriberKind.Azure, EngineMode.Azure, true, false, false)]
    [InlineData(TranscriberKind.Unknown, EngineMode.Azure, true, true, false)]
    public void Which_engines_a_retry_may_use(TranscriberKind target, EngineMode mode, bool offline, bool cloud, bool allowed) =>
        TranscriptRetry.MayUse(target, mode, offline, cloud).ShouldBe(allowed);

    [Fact]
    public async Task A_retry_replaces_the_record_in_place_and_links_back()
    {
        Directory.CreateDirectory(_directory);
        var store = new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl"));
        var audio = new RecentAudio();
        var parakeet = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("um I use cloud code");

        var older = new TranscriptRecord { At = DateTimeOffset.Now.AddMinutes(-5), Text = "older" };
        var heard = new TranscriptRecord
        {
            At = DateTimeOffset.Now.AddMinutes(-1),
            AudioSeconds = 1,
            Text = "I use clod code",
            Engine = TranscriberKind.Azure,
            Profile = CloudProfile.Fast,
        };
        var newer = new TranscriptRecord { At = DateTimeOffset.Now, Text = "newer" };
        store.Add(older);
        store.Add(heard);
        store.Add(newer);
        audio.Add(heard.Id, new float[AudioChunk.SampleRate]);

        var retry = new TranscriptRetry(
            audio,
            kind => kind == TranscriberKind.Parakeet ? parakeet : null,
            () => [DictionaryEntry.Correction("cloud code", "Claude Code")],
            () => new TextPipelineOptions());

        retry.TargetFor(heard).ShouldBe(TranscriberKind.Parakeet);
        retry.TargetFor(older).ShouldBeNull("the engine that heard it is not known");

        var replacement = await retry.RetryAsync(heard, TestContext.Current.CancellationToken);
        replacement.ShouldNotBeNull();
        replacement.Text.ShouldBe("I use Claude Code", "the same text pipeline a dictation gets");
        replacement.Engine.ShouldBe(TranscriberKind.Parakeet);
        replacement.Profile.ShouldBeNull();
        replacement.RetriedFrom.ShouldBe(heard.Id);
        replacement.At.ShouldBe(heard.At);

        retry.Apply(store, heard, replacement).ShouldBeTrue();

        store.Records.Select(r => r.Text).ShouldBe(["newer", "I use Claude Code", "older"]);
        audio.Contains(heard.Id).ShouldBeFalse();
        audio.Contains(replacement.Id).ShouldBeTrue("it can be retried back");

        // And it survives a reload with the link intact.
        var reloaded = new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl"));
        reloaded.Records[1].RetriedFrom.ShouldBe(heard.Id);
        reloaded.Records[1].Engine.ShouldBe(TranscriberKind.Parakeet);
    }

    [Fact]
    public async Task Without_its_audio_a_record_cannot_be_retried()
    {
        var parakeet = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("anything");
        var retry = new TranscriptRetry(new RecentAudio(), _ => parakeet, () => [], () => new TextPipelineOptions());
        var record = new TranscriptRecord { Text = "x", Engine = TranscriberKind.Azure };

        retry.TargetFor(record).ShouldBeNull();
        (await retry.RetryAsync(record, TestContext.Current.CancellationToken)).ShouldBeNull();
        parakeet.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task An_azure_retry_records_the_profile_for_the_cost_estimate()
    {
        var audio = new RecentAudio();
        var azure = new ScriptedTranscriber(TranscriberKind.Azure).Returns("heard in the cloud");
        var record = new TranscriptRecord { Text = "x", Engine = TranscriberKind.Parakeet, FellBack = true };
        audio.Add(record.Id, new float[AudioChunk.SampleRate]);

        var retry = new TranscriptRetry(
            audio, kind => kind == TranscriberKind.Azure ? azure : null, () => [], () => new TextPipelineOptions(),
            () => CloudProfile.Fast);

        var replacement = await retry.RetryAsync(record, TestContext.Current.CancellationToken);
        replacement.ShouldNotBeNull();
        replacement.Engine.ShouldBe(TranscriberKind.Azure);
        replacement.Profile.ShouldBe(CloudProfile.Fast);
        replacement.FellBack.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failing_engine_is_reported_to_the_caller()
    {
        var audio = new RecentAudio();
        var record = new TranscriptRecord { Text = "x", Engine = TranscriberKind.Parakeet };
        audio.Add(record.Id, new float[AudioChunk.SampleRate]);
        var azure = new ScriptedTranscriber(TranscriberKind.Azure)
            .Throws(new TranscriptionException(TranscriptionFailure.Unauthorized, "Key rejected", 401));

        var retry = new TranscriptRetry(audio, _ => azure, () => [], () => new TextPipelineOptions());

        await Should.ThrowAsync<TranscriptionException>(() => retry.RetryAsync(record, TestContext.Current.CancellationToken));
    }
}

/// <summary>Home's usage dashboard arithmetic.</summary>
public sealed class UsageStatsTests
{
    [Fact]
    public void Time_saved_is_typing_time_less_speaking_and_waiting()
    {
        // 80 words at 40 wpm is two minutes of typing; said in 30 s and transcribed in 2 s.
        var record = new TranscriptRecord
        {
            Text = string.Join(' ', Enumerable.Repeat("word", 80)),
            AudioSeconds = 30,
            ProcessingSeconds = 2,
            Engine = TranscriberKind.Parakeet,
        };

        var summary = UsageStats.Summarise([record]);

        summary.Words.ShouldBe(80);
        summary.AudioMinutes.ShouldBe(0.5);
        summary.TimeSaved.ShouldBe(TimeSpan.FromSeconds(120 - 30 - 2));

        // A faster typist saves less: 80 words at 80 wpm is one minute.
        UsageStats.Summarise([record], typingWordsPerMinute: 80).TimeSaved.ShouldBe(TimeSpan.FromSeconds(60 - 32));
    }

    [Fact]
    public void Time_saved_is_never_negative()
    {
        var slow = new TranscriptRecord { Text = "hi", AudioSeconds = 20, ProcessingSeconds = 5 };
        UsageStats.Summarise([slow]).TimeSaved.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void A_nonsense_typing_speed_falls_back_to_forty()
    {
        var record = new TranscriptRecord { Text = string.Join(' ', Enumerable.Repeat("w", 40)) };
        UsageStats.Summarise([record], typingWordsPerMinute: 0).TimeSaved.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Cloud_cost_counts_only_azure_minutes_at_the_profile_price()
    {
        TranscriptRecord[] records =
        [
            new() { Text = "a", AudioSeconds = 120, Engine = TranscriberKind.Azure, Profile = CloudProfile.Accurate },
            new() { Text = "b", AudioSeconds = 60, Engine = TranscriberKind.Azure, Profile = CloudProfile.Fast },
            // Older history with no profile is priced as Accurate, the dearer of the two.
            new() { Text = "c", AudioSeconds = 60, Engine = TranscriberKind.Azure },
            // Parakeet, fallback or not, costs nothing.
            new() { Text = "d", AudioSeconds = 600, Engine = TranscriberKind.Parakeet, FellBack = true },
            new() { Text = "e", AudioSeconds = 600 },
        ];

        var summary = UsageStats.Summarise(records, pricePerMinuteAccurate: 0.006, pricePerMinuteFast: 0.003);

        summary.EstimatedCloudCost.ShouldBe((2 * 0.006) + (1 * 0.003) + (1 * 0.006), 1e-9);
        summary.FellBack.ShouldBe(1);

        // The user's own prices.
        UsageStats.Summarise(records, pricePerMinuteAccurate: 0.01, pricePerMinuteFast: 0)
            .EstimatedCloudCost.ShouldBe(0.03, 1e-9);
    }

    [Fact]
    public void The_split_by_engine_puts_the_most_minutes_first()
    {
        TranscriptRecord[] records =
        [
            new() { Text = "one two", AudioSeconds = 60, Engine = TranscriberKind.Azure },
            new() { Text = "three", AudioSeconds = 120, Engine = TranscriberKind.Parakeet },
            new() { Text = "four five six", AudioSeconds = 60, Engine = TranscriberKind.Parakeet },
        ];

        var split = UsageStats.Summarise(records).ByEngine;

        split.Select(e => e.Engine).ShouldBe([TranscriberKind.Parakeet, TranscriberKind.Azure]);
        split[0].ShouldBe(new EngineUsage(TranscriberKind.Parakeet, 2, 4, 3));
        split[1].ShouldBe(new EngineUsage(TranscriberKind.Azure, 1, 2, 1));
    }

    [Fact]
    public void An_empty_history_is_all_zeros()
    {
        var summary = UsageStats.Summarise([]);
        summary.ShouldBe(summary with { Dictations = 0, Words = 0, AudioMinutes = 0, EstimatedCloudCost = 0 });
        summary.TimeSaved.ShouldBe(TimeSpan.Zero);
        summary.ByEngine.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(60, "1 min")]
    [InlineData(14 * 60 + 59, "14 min")]
    [InlineData(2 * 3600 + 5 * 60, "2 h 5 min")]
    public void Durations_read_the_way_people_say_them(int seconds, string expected) =>
        UsageStats.FormatDuration(TimeSpan.FromSeconds(seconds), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);

    [Theory]
    [InlineData(0, "$0")]
    [InlineData(0.004, "<$0.01")]
    [InlineData(0.421, "$0.42")]
    [InlineData(12.5, "$12.50")]
    public void Costs_are_dollars_and_cents(double dollars, string expected) =>
        UsageStats.FormatCost(dollars, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
}

/// <summary>History and settings written before these features load unchanged.</summary>
public sealed class BackwardCompatibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "murmur-compat", Guid.NewGuid().ToString("N"));

    public BackwardCompatibilityTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void A_history_line_from_an_older_build_loads_with_defaults()
    {
        var path = Path.Combine(_directory, "transcripts.jsonl");
        File.WriteAllText(path,
            """{"Id":"6a1f0f43-6f5b-4a4e-9d8c-0f6f0f1f2f3f","At":"2026-01-01T12:00:00+00:00","AudioSeconds":2.5,"ProcessingSeconds":0.3,"Text":"old line","Corrections":null}"""
            + Environment.NewLine);

        var record = new TranscriptStore(path).Records.ShouldHaveSingleItem();

        record.Id.ShouldBe(Guid.Parse("6a1f0f43-6f5b-4a4e-9d8c-0f6f0f1f2f3f"));
        record.Text.ShouldBe("old line");
        record.Engine.ShouldBe(TranscriberKind.Unknown);
        record.FellBack.ShouldBeFalse();
        record.Profile.ShouldBeNull();
        record.RetriedFrom.ShouldBeNull();
    }

    [Fact]
    public void New_fields_are_written_only_when_they_say_something()
    {
        var plain = JsonSerializer.Serialize(new TranscriptRecord { Text = "x" }, TranscriptJsonContext.Default.TranscriptRecord);
        plain.ShouldNotContain("Engine");
        plain.ShouldNotContain("FellBack");
        plain.ShouldNotContain("Profile");
        plain.ShouldNotContain("RetriedFrom");

        var from = Guid.NewGuid();
        var full = new TranscriptRecord
        {
            Text = "x",
            Engine = TranscriberKind.Parakeet,
            FellBack = true,
            Profile = CloudProfile.Fast,
            RetriedFrom = from,
        };

        var json = JsonSerializer.Serialize(full, TranscriptJsonContext.Default.TranscriptRecord);
        var back = JsonSerializer.Deserialize(json, TranscriptJsonContext.Default.TranscriptRecord);

        back.ShouldNotBeNull();
        back.Engine.ShouldBe(TranscriberKind.Parakeet);
        back.FellBack.ShouldBeTrue();
        back.Profile.ShouldBe(CloudProfile.Fast);
        back.RetriedFrom.ShouldBe(from);
    }

    [Fact]
    public void Settings_from_an_older_build_get_the_new_defaults()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """{"SettingsVersion":1,"PushToTalkKey":163,"Engine":0}""");

        using var settings = new AppSettings(path);
        var data = settings.Data;

        data.HandsFreeDoubleTap.ShouldBeTrue();
        data.PasteLastHotkey.ShouldBeNull("a global shortcut is never taken without the user choosing it");
        data.UnloadOfflineAfterMinutes.ShouldBe(SettingsData.DefaultUnloadOfflineAfterMinutes);
        data.AzureApiVersion.ShouldBe(new SettingsData().AzureApiVersion);
        data.CloudRequestTimeoutSeconds.ShouldBe(30);
        data.Language.ShouldBe(LanguageMode.Auto);
        data.TypingWordsPerMinute.ShouldBe(40);
        data.CloudPricePerMinuteAccurate.ShouldBe(0.006);
        data.CloudPricePerMinuteFast.ShouldBe(0.003);
    }

    [Fact]
    public void A_cleared_paste_shortcut_stays_cleared_across_a_restart()
    {
        var path = Path.Combine(_directory, "settings.json");
        using (var settings = new AppSettings(path))
        {
            settings.Update(settings.Data with { PasteLastHotkey = null, Language = LanguageMode.Hinglish });
        }

        File.ReadAllText(path).ShouldContain("\"PasteLastHotkey\": null");

        using var reloaded = new AppSettings(path);
        reloaded.Data.PasteLastHotkey.ShouldBeNull();
        reloaded.Data.Language.ShouldBe(LanguageMode.Hinglish);
    }
}

/// <summary>What each language mode sends, and what Settings warns about.</summary>
public sealed class LanguageModeTests
{
    [Theory]
    [InlineData(LanguageMode.Auto, null, null)]
    [InlineData(LanguageMode.Auto, "de", "de")]
    [InlineData(LanguageMode.English, "de", "en")]
    [InlineData(LanguageMode.Hindi, null, "hi")]
    [InlineData(LanguageMode.Hinglish, "hi", null)]
    public void The_language_code(LanguageMode mode, string? legacy, string? expected) =>
        LanguageModes.Code(mode, legacy).ShouldBe(expected);

    [Fact]
    public void Only_hinglish_adds_a_prompt_hint_and_it_asks_for_romanised_hindi()
    {
        LanguageModes.Hint(LanguageMode.Auto).ShouldBeNull();
        LanguageModes.Hint(LanguageMode.Hindi).ShouldBeNull();
        LanguageModes.Hint(LanguageMode.Hinglish).ShouldNotBeNull().ShouldContain("romanised Hindi");
        LanguageModes.Hint(LanguageMode.Hinglish)!.ShouldContain("not Devanagari");
    }

    [Fact]
    public void Parakeet_is_called_english_only_wherever_it_would_be_asked_for_hindi()
    {
        LanguageModes.Warning(LanguageMode.Hindi, EngineMode.Parakeet).ShouldNotBeNull().ShouldContain("English only");
        LanguageModes.Warning(LanguageMode.Hinglish, EngineMode.AzureWithParakeetFallback).ShouldNotBeNull().ShouldContain("fallback");
        LanguageModes.Warning(LanguageMode.Hinglish, EngineMode.Azure).ShouldBeNull();
        LanguageModes.Warning(LanguageMode.English, EngineMode.Parakeet).ShouldBeNull();
    }
}

/// <summary>The bar's badge and notices.</summary>
public sealed class BarNoticeTests
{
    [Fact]
    public void The_badge_says_where_the_words_were_heard()
    {
        BarNotices.BadgeFor(EngineMode.Parakeet).ShouldBe(PrivacyBadge.Local);
        BarNotices.BadgeFor(EngineMode.Azure).ShouldBe(PrivacyBadge.Cloud);
        BarNotices.BadgeFor(EngineMode.AzureWithParakeetFallback).ShouldBe(PrivacyBadge.Cloud);

        BarNotices.BadgeFor(TranscriberKind.Parakeet, fellBack: true, EngineMode.AzureWithParakeetFallback)
            .ShouldBe(PrivacyBadge.OfflineFallback);
        BarNotices.BadgeFor(TranscriberKind.Azure, fellBack: false, EngineMode.AzureWithParakeetFallback)
            .ShouldBe(PrivacyBadge.Cloud);
        BarNotices.BadgeFor(TranscriberKind.Unknown, fellBack: false, EngineMode.Parakeet).ShouldBe(PrivacyBadge.Local);

        BarNotices.Describe(PrivacyBadge.OfflineFallback).ShouldBe("Offline fallback");
        BarNotices.Describe(PrivacyBadge.OfflineFallback, LanguageMode.Hindi).ShouldBe("English-only fallback");
    }

    [Fact]
    public void A_fallback_notice_says_why_and_what_answered()
    {
        static TranscriptionFallback Because(Exception? cause) =>
            new(TranscriberKind.Azure, TranscriberKind.Parakeet, cause);

        BarNotices.ForFallback(Because(new HttpRequestException("down"))).ShouldBe("Azure unreachable — used Parakeet");
        BarNotices.ForFallback(Because(new TranscriptionException(TranscriptionFailure.Timeout, "slow")))
            .ShouldBe("Azure too slow — used Parakeet");
        BarNotices.ForFallback(Because(new TranscriptionException(TranscriptionFailure.RateLimited, "429")))
            .ShouldBe("Azure busy — used Parakeet");
        BarNotices.ForFallback(Because(null)).ShouldBe("Azure skipped — used Parakeet");
        BarNotices.ForFallback(Because(null), LanguageMode.Hinglish).ShouldStartWith("English-only fallback");
    }

    [Fact]
    public void A_lost_dictation_says_so_and_that_nothing_was_typed()
    {
        BarNotices.ForFault(new TranscriptionException(TranscriptionFailure.Unauthorized, "401"))
            .ShouldBe("Key rejected — nothing typed");
        BarNotices.ForFault(new TranscriptionException(TranscriptionFailure.Forbidden, "403"))
            .ShouldBe("Azure blocked this network — nothing typed", "a 403 is the network rules, not the key");
        BarNotices.ForFault(new InvalidOperationException()).ShouldBe("Transcription failed — nothing typed");
    }
}
