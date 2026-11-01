using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>Per-prompt key combinations, over a fake registration API.</summary>
public sealed class SnippetHotkeyBinderTests : IDisposable
{
    private static readonly HotkeyChord CtrlAlt1 = HotkeyChords.Parse("Ctrl+Alt+1");
    private static readonly HotkeyChord CtrlAlt2 = HotkeyChords.Parse("Ctrl+Alt+2");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"murmur-binder-{Guid.NewGuid():N}");
    private readonly SnippetStore _store;
    private readonly FakeGlobalHotkeys _hotkeys = new();
    private readonly RecordingTextInjector _injector = new();

    public SnippetHotkeyBinderTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new SnippetStore(Path.Combine(_directory, "snippets.jsonl"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static SnippetEntry Prompt(string trigger, HotkeyChord? chord, bool enabled = true) => new()
    {
        Trigger = trigger,
        Text = $"The {trigger} text.\nSecond line.",
        Match = SnippetMatch.WholeUtterance,
        Hotkey = chord,
        IsEnabled = enabled,
    };

    [Fact]
    public void Enabled_prompts_with_a_shortcut_are_registered()
    {
        var withKey = Prompt("review prompt", CtrlAlt1);
        _store.Add(withKey);
        _store.Add(Prompt("no key", null));
        _store.Add(Prompt("disabled", CtrlAlt2, enabled: false));

        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, _injector);

        _hotkeys.Registered.Values.ShouldBe([CtrlAlt1]);
        binder.StatusOf(withKey.Id).ShouldBe(SnippetHotkeyStatus.Active);
        binder.StatusOf(_store.Entries[1].Id).ShouldBe(SnippetHotkeyStatus.None);
        binder.StatusOf(_store.Entries[2].Id).ShouldBe(SnippetHotkeyStatus.None);
    }

    [Fact]
    public void Editing_the_store_re_registers()
    {
        var prompt = Prompt("review prompt", CtrlAlt1);
        _store.Add(prompt);
        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, _injector);
        var changes = 0;
        binder.Changed += (_, _) => changes++;

        _store.Update(prompt with { Hotkey = CtrlAlt2 });
        _hotkeys.Registered.Values.ShouldBe([CtrlAlt2]);

        _store.Update(prompt with { Hotkey = CtrlAlt2, IsEnabled = false });
        _hotkeys.Registered.ShouldBeEmpty();

        _store.Update(prompt);
        _store.Remove(prompt.Id);
        _hotkeys.Registered.ShouldBeEmpty();

        changes.ShouldBe(4);
    }

    [Fact]
    public void A_combination_another_program_holds_is_reported_not_hidden()
    {
        _hotkeys.Taken.Add(CtrlAlt1);
        var prompt = Prompt("review prompt", CtrlAlt1);
        _store.Add(prompt);

        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, _injector);

        binder.StatusOf(prompt.Id).ShouldBe(SnippetHotkeyStatus.InUse);
        SnippetHotkeyBinder.Describe(SnippetHotkeyStatus.InUse).ShouldNotBeNull();
        _hotkeys.Registered.ShouldBeEmpty();
    }

    [Fact]
    public void The_second_prompt_with_the_same_combination_is_a_duplicate()
    {
        var first = Prompt("first", CtrlAlt1);
        var second = Prompt("second", CtrlAlt1);
        _store.Add(first);
        _store.Add(second);

        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, _injector);

        binder.StatusOf(first.Id).ShouldBe(SnippetHotkeyStatus.Active);
        binder.StatusOf(second.Id).ShouldBe(SnippetHotkeyStatus.Duplicate);
        _hotkeys.Registered.Count.ShouldBe(1);
    }

    [Fact]
    public void A_combination_that_would_steal_typing_is_never_registered()
    {
        var prompt = Prompt("review prompt", HotkeyChords.Parse("Shift+A"));
        _store.Add(prompt);

        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, _injector);

        binder.StatusOf(prompt.Id).ShouldBe(SnippetHotkeyStatus.Invalid);
        _hotkeys.RegisterCalls.ShouldBe(0);
    }

    [Fact]
    public void Without_a_registration_api_every_shortcut_is_unsupported()
    {
        var prompt = Prompt("review prompt", CtrlAlt1);
        _store.Add(prompt);

        using var binder = new SnippetHotkeyBinder(_store, hotkeys: null, _injector);

        binder.IsSupported.ShouldBeFalse();
        binder.StatusOf(prompt.Id).ShouldBe(SnippetHotkeyStatus.Unsupported);
    }

    [Fact]
    public async Task Pressing_the_combination_pastes_the_prompt_verbatim()
    {
        var prompt = Prompt("review prompt", CtrlAlt1);
        _store.Add(prompt);
        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, _injector);

        _hotkeys.Press(CtrlAlt1).ShouldBeTrue();
        await binder.WhenIdleAsync();

        _injector.Injected.ShouldBe([prompt.Text]);
        _injector.Verbatim.ShouldBe([true]);
        _injector.EnterPresses.ShouldBe(0);
    }

    [Fact]
    public async Task A_recording_in_progress_is_cancelled_before_the_paste()
    {
        var prompt = Prompt("review prompt", CtrlAlt1);
        _store.Add(prompt);
        var order = new List<string>();
        var injector = new OrderedInjector(order);

        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, injector, () =>
        {
            order.Add("cancel");
            return Task.CompletedTask;
        });

        _hotkeys.Press(CtrlAlt1);
        await binder.WhenIdleAsync();

        order.ShouldBe(["cancel", "inject:" + prompt.Text]);
    }

    [Fact]
    public async Task Cancelling_discards_the_audio_and_types_nothing()
    {
        // The real engine, so "cancel" is proved against the state machine rather than a lambda.
        var hotkey = new FakeHotkeySource();
        var transcriber = new FakeTranscriber("half a sentence");
        var dictation = new RecordingTextInjector();
        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(1.0), hotkey, transcriber, dictation, () => [], new FakeClock());

        var prompt = Prompt("review prompt", CtrlAlt1);
        _store.Add(prompt);
        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, _injector, engine.CancelAsync);

        hotkey.Press();
        await DictationEngineTests.UntilAsync(() => engine.State == DictationState.Recording);
        await DictationEngineTests.UntilAsync(() => engine.Level > 0);
        engine.State.ShouldBe(DictationState.Recording);

        _hotkeys.Press(CtrlAlt1);
        await binder.WhenIdleAsync();
        engine.State.ShouldBe(DictationState.Idle);

        // The push-to-talk key comes up afterwards and finds nothing to finish.
        hotkey.Release();
        for (var i = 0; i < 200; i++) await Task.Yield();

        transcriber.SegmentLengths.ShouldBeEmpty();
        dictation.Injected.ShouldBeEmpty();
        _injector.Injected.ShouldBe([prompt.Text]);
    }

    [Fact]
    public async Task A_disabled_or_deleted_prompt_does_not_fire()
    {
        var prompt = Prompt("review prompt", CtrlAlt1);
        _store.Add(prompt);
        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, _injector);

        _store.Remove(prompt.Id);
        await binder.FireAsync(prompt.Id);

        _hotkeys.Press(CtrlAlt1).ShouldBeFalse();
        _injector.Injected.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_paste_is_reported()
    {
        var prompt = Prompt("review prompt", CtrlAlt1);
        _store.Add(prompt);
        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, new ThrowingInjector());
        Exception? reported = null;
        binder.FireFaulted += (_, ex) => reported = ex;

        _hotkeys.Press(CtrlAlt1);
        await binder.WhenIdleAsync();

        reported.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public void Suspending_gives_every_combination_back_until_resumed()
    {
        var prompt = Prompt("review prompt", CtrlAlt1);
        _store.Add(prompt);
        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, _injector);

        var suspension = binder.Suspend();
        _hotkeys.Registered.ShouldBeEmpty();
        binder.StatusOf(prompt.Id).ShouldBe(SnippetHotkeyStatus.Active);

        suspension.Dispose();
        suspension.Dispose();
        _hotkeys.Registered.Values.ShouldBe([CtrlAlt1]);
    }

    [Fact]
    public void Checking_a_combination_reports_why_it_would_not_work()
    {
        var first = Prompt("first", CtrlAlt1);
        _store.Add(first);
        _hotkeys.Taken.Add(HotkeyChords.Parse("Ctrl+Alt+9"));
        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, _injector);
        var other = Guid.NewGuid();

        binder.Check(CtrlAlt1, other).ShouldBe(SnippetHotkeyStatus.Duplicate);
        binder.Check(CtrlAlt1, first.Id).ShouldBe(SnippetHotkeyStatus.Active);
        binder.Check(HotkeyChords.Parse("Ctrl+Alt+9"), other).ShouldBe(SnippetHotkeyStatus.InUse);
        binder.Check(HotkeyChords.Parse("Shift+Q"), other).ShouldBe(SnippetHotkeyStatus.Invalid);
        binder.Check(CtrlAlt2, other).ShouldBe(SnippetHotkeyStatus.Active);

        // The probe is given straight back.
        _hotkeys.Registered.Values.ShouldBe([CtrlAlt1]);
    }

    [Fact]
    public void Disposing_unregisters_and_stops_following_the_store()
    {
        _store.Add(Prompt("review prompt", CtrlAlt1));
        var binder = new SnippetHotkeyBinder(_store, _hotkeys, _injector);

        binder.Dispose();
        _hotkeys.Registered.ShouldBeEmpty();

        _store.Add(Prompt("another", CtrlAlt2));
        _hotkeys.Registered.ShouldBeEmpty();
    }

    [Fact]
    public void A_press_while_re_registering_does_not_wait_on_the_registration()
    {
        // The real API raises Pressed on the thread that Register waits on. A handler that
        // waited for the binder in turn would stall every registration and lose them all.
        var prompt = Prompt("review prompt", CtrlAlt1);
        _store.Add(prompt);
        var hotkeys = new PumpLikeHotkeys();
        using var binder = new SnippetHotkeyBinder(_store, hotkeys, _injector);

        hotkeys.PressDuringRegister = 1;
        _store.Add(Prompt("another", CtrlAlt2));

        hotkeys.PressReturnedInTime.ShouldBe(true);
        binder.StatusOf(prompt.Id).ShouldBe(SnippetHotkeyStatus.Active);
        binder.StatusOf(_store.Entries[1].Id).ShouldBe(SnippetHotkeyStatus.Active);
    }

    [Fact]
    public void The_suggestion_is_the_first_free_left_hand_combination()
    {
        var first = Prompt("first", CtrlAlt1);
        _store.Add(first);
        _hotkeys.Taken.Add(CtrlAlt2);
        using var binder = new SnippetHotkeyBinder(_store, _hotkeys, _injector);

        binder.Suggest(Guid.NewGuid()).ShouldBe(HotkeyChords.Parse("Ctrl+Alt+3"));
        binder.Suggest(first.Id).ShouldBe(CtrlAlt1);
        _hotkeys.Registered.Values.ShouldBe([CtrlAlt1]);

        using var unsupported = new SnippetHotkeyBinder(_store, hotkeys: null, _injector);
        unsupported.Suggest(Guid.NewGuid()).ShouldBeNull();
    }

    /// <summary>Raises a press on another thread from inside Register, and waits for it, as the pump does.</summary>
    private sealed class PumpLikeHotkeys : IGlobalHotkeys
    {
        public int? PressDuringRegister { get; set; }

        public bool? PressReturnedInTime { get; private set; }

        public event EventHandler<int>? Pressed;

        public bool Register(int id, HotkeyChord chord)
        {
            if (PressDuringRegister is { } pressed)
            {
                PressDuringRegister = null;
                PressReturnedInTime = Task.Run(() => Pressed?.Invoke(this, pressed)).Wait(TimeSpan.FromSeconds(5));
            }

            return true;
        }

        public void Unregister(int id)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class OrderedInjector(List<string> order) : ITextInjector
    {
        public ValueTask<bool> InjectAsync(string text, CancellationToken cancellationToken)
        {
            order.Add("inject:" + text);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> PressEnterAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class ThrowingInjector : ITextInjector
    {
        public ValueTask<bool> InjectAsync(string text, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("SendInput refused.");

        public ValueTask<bool> PressEnterAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}

/// <summary>The engine side of saved prompts.</summary>
public sealed class SavedPromptEngineTests
{
    [Fact]
    public async Task A_spoken_prompt_is_injected_verbatim_and_then_submitted()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        var prompt = new SnippetEntry
        {
            Trigger = "review prompt",
            Text = "Line one\nLine two",
            Match = SnippetMatch.WholeUtterance,
        };

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(1.0), hotkey, new FakeTranscriber("Review prompt. Press enter."), injector,
            () => [], new FakeClock(),
            options: () => new TextPipelineOptions { Snippets = [prompt] });

        await DictationEngineTests.DictateAsync(hotkey, engine);

        injector.Injected.ShouldBe(["Line one\nLine two"]);
        injector.Verbatim.ShouldBe([true]);
        injector.InjectedCountAtFirstEnter.ShouldBe(1);
    }

    [Fact]
    public async Task Cancelling_when_not_recording_does_nothing()
    {
        await using var engine = new DictationEngine(
            FakeAudioCapture.Silence(0.1), new FakeHotkeySource(), new FakeTranscriber(), new RecordingTextInjector(),
            () => [], new FakeClock());

        await engine.CancelAsync();

        engine.State.ShouldBe(DictationState.Idle);
    }
}
