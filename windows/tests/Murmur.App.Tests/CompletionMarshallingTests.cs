using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.App.Views;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;

namespace Murmur.AppTests;

/// <summary>
/// Regression tests for the cross-thread completion path, against the real Avalonia view.
/// </summary>
/// <remarks>
/// <para>
/// The shipped bug: <c>DictationEngine.Completed</c> fires on a background thread, the
/// history subscriber called <c>TranscriptStore.Add</c> there, <c>Changed</c> reached
/// <see cref="TranscriptionsView"/>, and its refresh touched live controls off the UI
/// thread. Avalonia threw, the exception was swallowed in the engine, and — because it
/// unwound before injection — no text was ever typed either.
/// </para>
/// <para>
/// These tests use the real headless Avalonia platform, a real <see cref="TranscriptStore"/>
/// and a real <see cref="TranscriptionsView"/>, with events raised from genuine background
/// threads, so this exact class of failure shows up here instead of on a user's machine.
/// </para>
/// </remarks>
public sealed class CompletionMarshallingTests
{
    private static string TempStorePath() =>
        Path.Combine(Path.GetTempPath(), "murmur-tests", $"{Guid.NewGuid():N}.jsonl");

    private static void OnBackgroundThread(Action action, out Exception? escaped)
    {
        Exception? thrown = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { thrown = ex; }
        })
        {
            IsBackground = true,
            Name = "test background completion",
        };

        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue("the background thread must not hang");
        escaped = thrown;
    }

    private static bool ViewShowsText(Control view, string text) =>
        view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text);

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        condition().ShouldBeTrue(because);
    }

    /// <summary>Counts fully-drained capture enumerations, so waits are race-free.</summary>
    private sealed class TrackingCapture(Murmur.Abstractions.IAudioCapture inner)
        : Murmur.Abstractions.IAudioCapture
    {
        private int _drained;

        public int DrainedCount => Volatile.Read(ref _drained);

        public bool IsCapturing => inner.IsCapturing;

        public async IAsyncEnumerable<Murmur.Abstractions.AudioChunk> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var chunk in inner.CaptureAsync(cancellationToken)) yield return chunk;
            Interlocked.Increment(ref _drained);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// The guard that keeps this file honest: headless Avalonia really does throw on
    /// off-thread control access, so a green run of the tests below is meaningful.
    /// </summary>
    [AvaloniaFact]
    public void Headless_avalonia_enforces_ui_thread_affinity()
    {
        var panel = new StackPanel();
        var window = new Window { Content = panel };
        window.Show();

        // The pre-fix wiring in miniature: touch live controls from a raw background thread.
        OnBackgroundThread(() => panel.Children.Add(new TextBlock()), out var escaped);

        escaped.ShouldNotBeNull("off-thread control access must throw, or these tests prove nothing");
    }

    [AvaloniaFact]
    public void A_background_thread_store_add_updates_the_view_instead_of_crashing()
    {
        var store = new TranscriptStore(TempStorePath());
        var view = new TranscriptionsView(store);
        var window = new Window { Content = view };
        window.Show();

        var record = new TranscriptRecord { At = DateTimeOffset.Now, Text = "added off-thread" };

        // Pre-fix, this throws InvalidOperationException ("call from invalid thread") right
        // here — the exact exception that silently killed completion in the shipped app.
        OnBackgroundThread(() => store.Add(record), out var escaped);
        escaped.ShouldBeNull($"a background-thread Add must not blow up in the view, but got: {escaped}");

        // The view marshalled its refresh; run the posted job and it must show the record.
        Dispatcher.UIThread.RunJobs();
        ViewShowsText(view, "added off-thread").ShouldBeTrue("the new record must be visible");
        store.Records.ShouldHaveSingleItem();
    }

    [AvaloniaFact]
    public async Task Full_pipeline_from_a_background_thread_reaches_the_view_the_store_and_the_injector()
    {
        var store = new TranscriptStore(TempStorePath());
        var view = new TranscriptionsView(store);
        var window = new Window { Content = view };
        window.Show();

        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        var capture = new TrackingCapture(FakeAudioCapture.Tone(0.4));

        await using var engine = new DictationEngine(
            capture, hotkey, new FakeTranscriber("dictated for the ui"), injector, () => []);

        // Wired exactly the way Composition wires it: build the record on the engine's
        // thread, post only the store update to the UI thread.
        engine.Completed += (_, result) =>
        {
            var record = new TranscriptRecord
            {
                At = result.At,
                AudioSeconds = result.AudioDuration.TotalSeconds,
                ProcessingSeconds = result.ProcessingTime.TotalSeconds,
                Text = result.Text,
                Corrections = result.Corrections.Count > 0 ? result.Corrections : null,
            };
            Dispatcher.UIThread.Post(() => store.Add(record));
        };

        OnBackgroundThread(hotkey.Press, out var pressEscaped);
        pressEscaped.ShouldBeNull();

        // Let the fake capture drain fully so the buffer is non-empty, then release.
        await WaitForAsync(() => capture.DrainedCount > 0, "the fake capture should drain");

        OnBackgroundThread(hotkey.Release, out var releaseEscaped);
        releaseEscaped.ShouldBeNull();

        await WaitForAsync(() => engine.State == DictationState.Idle, "the engine should return to idle");

        injector.Injected.ShouldBe(["dictated for the ui"], "the text must reach the focused app");

        // The posted history update runs on the dispatcher; flush it and check both sinks.
        Dispatcher.UIThread.RunJobs();

        store.Records.ShouldHaveSingleItem().Text.ShouldBe("dictated for the ui");
        ViewShowsText(view, "dictated for the ui").ShouldBeTrue("the view must show the dictation");
    }

    [AvaloniaFact]
    public async Task KeepHistory_off_skips_the_store_entirely_but_still_injects()
    {
        var store = new TranscriptStore(TempStorePath());
        var view = new TranscriptionsView(store);
        var window = new Window { Content = view };
        window.Show();

        var settings = new SettingsData { KeepHistory = false };
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        var capture = new TrackingCapture(FakeAudioCapture.Tone(0.4));

        await using var engine = new DictationEngine(
            capture, hotkey, new FakeTranscriber("history is off"), injector, () => []);

        // Composition's handler shape, including the early return before any marshalling.
        engine.Completed += (_, result) =>
        {
            if (!settings.KeepHistory) return;

            var record = new TranscriptRecord { At = result.At, Text = result.Text };
            Dispatcher.UIThread.Post(() => store.Add(record));
        };

        OnBackgroundThread(hotkey.Press, out _);
        await WaitForAsync(() => capture.DrainedCount > 0, "the fake capture should drain");

        OnBackgroundThread(hotkey.Release, out _);
        await WaitForAsync(() => engine.State == DictationState.Idle, "the engine should return to idle");

        Dispatcher.UIThread.RunJobs();

        injector.Injected.ShouldBe(["history is off"], "injection is independent of history");
        store.Records.ShouldBeEmpty("history was off — nothing may be recorded");
        ViewShowsText(view, "No recordings yet").ShouldBeTrue("the empty state must remain");
    }
}
