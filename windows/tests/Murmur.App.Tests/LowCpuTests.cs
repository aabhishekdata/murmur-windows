using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.App;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.App.Views;
using Murmur.Core;
using Murmur.Speech;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.AppTests;

/// <summary>
/// An idle bar must cost nothing: no animation frames, no polling, no allocations per read.
/// </summary>
public sealed class IdleCostTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(16);

    /// <summary>Delivers frames until the row settles, as the compositor would.</summary>
    private static int RunUntilSettled(WaveBars wave)
    {
        var now = TimeSpan.FromSeconds(10);
        var frames = 0;

        while (wave.IsRequestingFrames && frames < 10_000)
        {
            now += Step;
            wave.OnFrame(now);
            frames++;
        }

        return frames;
    }

    [AvaloniaFact]
    public void A_bar_at_rest_requests_no_frames()
    {
        // The single most important number in this phase: the bar sits on screen all day.
        var bar = new FlowBarWindow();
        bar.Show();

        bar.State.ShouldBe(BarState.Idle);
        bar.Wave.IsRequestingFrames.ShouldBeFalse("idle bars are drawn once and left alone");
        bar.Wave.IsSettled.ShouldBeTrue();
        bar.Wave.IsVisible.ShouldBeFalse("no level at rest, so no bars to animate");
    }

    [AvaloniaFact]
    public void Listening_animates_and_stopping_settles_back_to_no_frames()
    {
        var bar = new FlowBarWindow();
        bar.Show();
        var wave = bar.Wave;

        bar.ApplyState(BarState.Listening);
        wave.IsRequestingFrames.ShouldBeTrue("a live row moves");

        wave.Level = 0.6;
        var now = TimeSpan.FromSeconds(1);
        for (var i = 0; i < 30; i++) wave.OnFrame(now += Step);
        wave.BarHeights.Max().ShouldBeGreaterThan(Tokens.Wave.IdleFraction * 2);

        // Stopping hides the bars, which puts them straight back at rest: nothing is spent
        // animating bars nobody can see, and the next recording starts from flat.
        bar.ApplyState(BarState.Idle);
        wave.OnFrame(now += Step);

        wave.IsSettled.ShouldBeTrue();
        wave.IsRequestingFrames.ShouldBeFalse("once hidden it asks for nothing more");
        RunUntilSettled(wave).ShouldBe(0);
    }

    [AvaloniaFact]
    public void Transcribing_requests_no_frames_at_all()
    {
        // The pulse dots that used to loop here are gone: "Transcribing" is a word, and a word
        // does not need a frame clock.
        var bar = new FlowBarWindow();
        bar.Show();

        bar.ApplyState(BarState.Processing);

        bar.Wave.IsRequestingFrames.ShouldBeFalse();
        bar.GetVisualDescendants().OfType<WaveBars>().ShouldAllBe(w => !w.IsRequestingFrames);
    }

    [AvaloniaFact]
    public void The_bars_only_transitions_are_short_fades_of_opacity()
    {
        // No layout property is ever animated — the window resizes once per state change, never
        // per frame — and nothing runs longer than a glance.
        var bar = new FlowBarWindow();
        bar.Show();

        var transitions = bar.GetVisualDescendants()
            .OfType<Avalonia.Animation.Animatable>()
            .Where(a => a.Transitions is not null)
            .SelectMany(a => a.Transitions!)
            .ToList();

        transitions.ShouldNotBeEmpty();
        foreach (var transition in transitions)
        {
            var timed = transition.ShouldBeAssignableTo<Avalonia.Animation.TransitionBase>()!;
            timed.Duration.ShouldBeLessThanOrEqualTo(Tokens.Motion.Ceiling);
            timed.Delay.ShouldBe(TimeSpan.Zero);

            // Colours change at once: an animated colour is a new brush every frame.
            timed.Property.ShouldBe(Avalonia.Visual.OpacityProperty, $"{timed.Property!.Name} is not opacity");
        }
    }

    [AvaloniaFact]
    public void The_compositor_actually_delivers_the_requested_frames()
    {
        // Everything else here drives OnFrame by hand; this proves the real request path is
        // answered, so the bars do move in the running app.
        var wave = new WaveBars { Width = 90, Height = 18, IsActive = true, Level = 0.6 };
        var window = new Window { Content = wave };
        window.Show();

        for (var i = 0; i < 60; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        wave.BarHeights.Max().ShouldBeGreaterThan(Tokens.Wave.IdleFraction * 1.5);
        wave.IsRequestingFrames.ShouldBeTrue("still live, still animating");
    }

    [AvaloniaFact]
    public void A_row_that_is_not_on_screen_requests_nothing()
    {
        var wave = new WaveBars { IsActive = true, Level = 0.5 };
        wave.IsRequestingFrames.ShouldBeFalse("there is no top level to animate for");
    }

    [AvaloniaFact]
    public void The_level_is_read_from_its_source_on_the_frame_clock()
    {
        var reads = 0;
        var wave = new WaveBars { LevelSource = () => { reads++; return 0.7; } };
        var window = new Window { Content = wave };
        window.Show();

        reads.ShouldBe(0, "nothing is sampled while the row is at rest");

        wave.IsActive = true;
        wave.OnFrame(TimeSpan.FromSeconds(1));

        reads.ShouldBe(1);
        wave.Level.ShouldBe(0.7);
    }

    [AvaloniaFact]
    public void Painting_is_capped_while_the_physics_keep_their_pace()
    {
        // Frames delivered at 60 Hz step the physics in pairs at 30 Hz: the same motion, half
        // the repaints.
        var reads = 0;
        var wave = new WaveBars { IsActive = true, LevelSource = () => { reads++; return 0.5; } };
        var window = new Window { Content = wave };
        window.Show();

        var now = TimeSpan.FromSeconds(1);
        for (var i = 0; i < 61; i++) wave.OnFrame(now += Step);

        reads.ShouldBeInRange(28, 33);
    }
}

/// <summary>Shared brushes.</summary>
public sealed class BrushCacheTests
{
    [AvaloniaFact]
    public void A_token_brush_is_the_same_object_on_every_read()
    {
        Tokens.Brushes.Ink.ShouldBeSameAs(Tokens.Brushes.Ink);
        BrushCache.Get(Tokens.Colors.BarWave, 0.5).ShouldBeSameAs(BrushCache.Get(Tokens.Colors.BarWave, 0.5));
    }

    [AvaloniaFact]
    public void An_animated_opacity_cannot_grow_the_cache_without_bound()
    {
        for (var i = 0; i < 10_000; i++) BrushCache.Get(Tokens.Colors.BarWave, i / 10_000.0);

        BrushCache.Count.ShouldBeLessThanOrEqualTo(BrushCache.OpacitySteps + 64);
    }

    [AvaloniaFact]
    public void Switching_theme_empties_the_cache_and_resolves_the_new_palette()
    {
        var app = Avalonia.Application.Current!;
        var original = app.RequestedThemeVariant;

        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            var light = Tokens.Brushes.Ink;
            BrushCache.Count.ShouldBeGreaterThan(0);

            app.RequestedThemeVariant = ThemeVariant.Dark;
            BrushCache.Count.ShouldBe(0, "the theme change drops the old palette's brushes");

            var dark = Tokens.Brushes.Ink;
            dark.ShouldNotBeSameAs(light);
            ((Avalonia.Media.ISolidColorBrush)dark).Color.ShouldBe(Tokens.Colors.Ink);
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }
}

/// <summary>The history list: virtualised and debounced.</summary>
public sealed class TranscriptListTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"murmur-list-{Guid.NewGuid():N}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private TranscriptStore StoreWith(int count, Func<int, string>? text = null)
    {
        var store = new TranscriptStore(_path);
        var start = DateTimeOffset.Now;

        for (var i = 0; i < count; i++)
        {
            store.Add(new TranscriptRecord
            {
                At = start - TimeSpan.FromMinutes(i),
                Text = text?.Invoke(i) ?? $"dictation number {i}",
            });
        }

        return store;
    }

    [AvaloniaFact]
    public void A_long_history_builds_cards_only_for_what_is_on_screen()
    {
        var store = StoreWith(500);
        var view = new TranscriptionsView(store);
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();

        view.ItemCount.ShouldBeGreaterThan(500, "every record plus its day heading is listed");

        var cards = view.GetVisualDescendants().OfType<Border>()
            .Count(b => b.Child is StackPanel panel && panel.Children.OfType<Grid>().Any());
        cards.ShouldBeGreaterThan(0);
        cards.ShouldBeLessThan(100, "off-screen rows are not built");
    }

    [AvaloniaFact]
    public void Typing_in_search_filters_once_typing_pauses()
    {
        var store = StoreWith(20, i => i == 7 ? "the special one" : $"ordinary {i}");
        var view = new TranscriptionsView(store);
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();

        var before = view.ItemCount;
        var search = view.GetVisualDescendants().OfType<TextBox>().First();

        search.Text = "s";
        search.Text = "spe";
        search.Text = "special";

        // TextBox raises TextChanged through the dispatcher, not inline.
        Dispatcher.UIThread.RunJobs();

        view.IsSearchPending.ShouldBeTrue();
        view.ItemCount.ShouldBe(before, "no keystroke filters on its own");

        view.ApplySearchNow();

        view.IsSearchPending.ShouldBeFalse();
        view.ItemCount.ShouldBe(2, "one heading and the one match");
    }

    [AvaloniaFact]
    public async Task The_debounced_search_applies_by_itself()
    {
        var store = StoreWith(5, i => i == 2 ? "needle" : $"hay {i}");
        var view = new TranscriptionsView(store);
        var window = new Window { Content = view };
        window.Show();

        view.GetVisualDescendants().OfType<TextBox>().First().Text = "needle";
        Dispatcher.UIThread.RunJobs();
        view.IsSearchPending.ShouldBeTrue();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (view.IsSearchPending && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        view.IsSearchPending.ShouldBeFalse();
        view.ItemCount.ShouldBe(2);
    }
}

/// <summary>The cached model check, and the setting it used to ignore.</summary>
public sealed class ModelLocatorTests
{
    private const string Configured = @"D:\models\mine";
    private const string Default = @"C:\Users\x\AppData\Local\Murmur\models\parakeet-v2";

    [Fact]
    public void A_configured_model_folder_is_honoured()
    {
        var locator = new ModelLocator(
            () => Configured, () => [Default], d => d == Configured);

        locator.Directory.ShouldBe(Configured);
        locator.IsInstalled.ShouldBeTrue("the bar used to say No model here");
    }

    [Fact]
    public void An_incomplete_configured_folder_falls_back_to_the_standard_paths()
    {
        var locator = new ModelLocator(() => Configured, () => [Default], d => d == Default);
        locator.Directory.ShouldBe(Default);
    }

    [Fact]
    public void The_answer_is_cached_rather_than_re_read_from_disk()
    {
        var checks = 0;
        var locator = new ModelLocator(() => null, () => [Default], _ => { checks++; return true; });
        var afterConstruction = checks;

        for (var i = 0; i < 1000; i++) _ = locator.IsInstalled;

        checks.ShouldBe(afterConstruction);
    }

    [Fact]
    public void Refresh_reports_a_change_and_only_a_change()
    {
        var installed = false;
        var locator = new ModelLocator(() => null, () => [Default], _ => installed);
        var raised = 0;
        locator.Changed += (_, _) => raised++;

        locator.Refresh().ShouldBeFalse();
        raised.ShouldBe(0);

        installed = true;
        locator.Refresh().ShouldBeTrue();
        raised.ShouldBe(1);

        locator.Refresh();
        raised.ShouldBe(1);
    }
}

/// <summary>Parakeet's threading and copy avoidance.</summary>
public sealed class ParakeetOptionsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(6, 3)]
    [InlineData(8, 4)]
    [InlineData(16, 4)]
    [InlineData(64, 4)]
    public void Automatic_threads_are_half_the_processors_between_one_and_four(int processors, int expected) =>
        ParakeetOptions.AutoThreads(processors).ShouldBe(expected);

    [Fact]
    public void An_explicit_thread_count_wins() =>
        new ParakeetOptions { Threads = 6 }.ResolvedThreads.ShouldBe(6);

    [Fact]
    public void Automatic_is_the_default() =>
        new ParakeetOptions().ResolvedThreads.ShouldBe(ParakeetOptions.AutoThreads(Environment.ProcessorCount));

    [Fact]
    public void A_whole_array_is_handed_to_the_recogniser_without_copying()
    {
        var samples = new float[16000];
        ParakeetTranscriber.WholeArray(samples).ShouldBeSameAs(samples);
    }

    [Fact]
    public void A_slice_is_copied_because_the_recogniser_takes_no_offset()
    {
        var samples = new float[] { 1, 2, 3, 4 };
        var slice = ParakeetTranscriber.WholeArray(samples.AsMemory(1, 2));

        slice.ShouldNotBeSameAs(samples);
        slice.ShouldBe([2f, 3f]);
    }
}

/// <summary>Settings that now apply while the app runs.</summary>
public sealed class LiveSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"murmur-live-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Choosing_a_new_push_to_talk_key_rearms_the_hook_without_a_restart()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var hotkey = new FakeHotkeySource { VirtualKey = settings.Data.PushToTalkKey };

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(0.2), hotkey, new FakeTranscriber("x"),
            new RecordingTextInjector(), () => []);

        Composition.ApplyLive(settings, new ModelLocator(() => null, () => [], _ => false), engine, cancellationToken: TestContext.Current.CancellationToken);

        settings.Update(settings.Data with { PushToTalkKey = 0x7C });

        hotkey.VirtualKey.ShouldBe(0x7C);
    }

    [Fact]
    public void Changing_the_model_folder_re_checks_for_the_model()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var models = new ModelLocator(
            () => settings.Data.ModelDirectory, () => [], d => d == @"D:\model");
        var raised = 0;
        models.Changed += (_, _) => raised++;

        Composition.ApplyLive(settings, models, engine: null, cancellationToken: TestContext.Current.CancellationToken);
        models.IsInstalled.ShouldBeFalse();

        settings.Update(settings.Data with { ModelDirectory = @"D:\model" });

        models.IsInstalled.ShouldBeTrue();
        raised.ShouldBe(1);
    }
}
