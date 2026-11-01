using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.Abstractions;
using Murmur.App;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.App.Views;
using Murmur.Cloud;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.AppTests;

/// <summary>
/// Hands-free and notices on the bar, paste-last in Settings, retry in History, the usage
/// dashboard, and the language the cloud is asked for.
/// </summary>
public sealed class CompetitorFeatureUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "murmur-tests", Guid.NewGuid().ToString("N"));

    public CompetitorFeatureUiTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private AppSettings Settings() => new(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());

    private Composition Preview(
        AppSettings settings,
        TranscriptStore? transcripts = null,
        IGlobalHotkeys? hotkeys = null,
        ITextInjector? injector = null,
        Func<TranscriberKind, ITranscriber?>? engines = null) =>
        Composition.ForPreview(
            settings,
            new DictionaryFile(Path.Combine(_directory, "dictionary.jsonl")),
            transcripts ?? new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl")),
            secrets: new InMemorySecretStore(),
            hotkeys: hotkeys,
            injector: injector,
            engines: engines);

    private static T Show<T>(T view) where T : Control
    {
        var window = new Window { Width = 900, Height = 1600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    // ---- The bar ----

    [AvaloniaFact]
    public void Hands_free_says_so_and_keeps_the_level_bars()
    {
        var bar = new FlowBarWindow();
        bar.Show();

        bar.ApplyState(BarState.HandsFree);

        bar.Label.ShouldBe("Hands-free — tap to stop");
        bar.Wave.IsVisible.ShouldBeTrue();
        bar.Cancel.IsVisible.ShouldBeTrue();
        bar.Width.ShouldBeGreaterThan(Tokens.Bar.ListeningWidth, "room for the words");
    }

    [AvaloniaFact]
    public void The_badge_follows_the_engine_and_turns_to_offline_fallback_when_parakeet_answered()
    {
        using var settings = Settings();
        var composition = Preview(settings);
        var bar = new FlowBarWindow(composition);
        bar.Show();

        bar.Badge.ShouldBe(PrivacyBadge.Local);
        bar.BadgeText.ShouldBe("Local — on this PC");

        settings.Update(settings.Data with { Engine = EngineMode.AzureWithParakeetFallback });
        Dispatcher.UIThread.RunJobs();
        bar.Badge.ShouldBe(PrivacyBadge.Cloud);

        bar.ShowOutcome(TranscriberKind.Parakeet, fellBack: true);
        bar.Badge.ShouldBe(PrivacyBadge.OfflineFallback);
        bar.BadgeText.ShouldBe("Offline fallback");

        // Hindi heard by an English-only fallback says so.
        settings.Update(settings.Data with { Language = LanguageMode.Hindi });
        Dispatcher.UIThread.RunJobs();
        bar.BadgeText.ShouldBe("English-only fallback");

        // The next dictation Azure answers puts the cloud back.
        bar.ShowOutcome(TranscriberKind.Azure, fellBack: false);
        bar.Badge.ShouldBe(PrivacyBadge.Cloud);

        // And in Parakeet mode there is nothing to fall back from.
        bar.ShowOutcome(TranscriberKind.Parakeet, fellBack: true);
        settings.Update(settings.Data with { Engine = EngineMode.Parakeet });
        Dispatcher.UIThread.RunJobs();
        bar.Badge.ShouldBe(PrivacyBadge.Local);
    }

    [AvaloniaFact]
    public void The_badge_is_never_red()
    {
        var bar = new FlowBarWindow();
        bar.Show();

        var badge = bar.GetVisualDescendants().OfType<FluentIcon>().First();
        var colour = ((Avalonia.Media.ISolidColorBrush)badge.Foreground!).Color;
        AccentPolicy.IsRed(colour).ShouldBeFalse();
        colour.ShouldBe(Tokens.Colors.BarInkMuted);
    }

    [AvaloniaFact]
    public void A_notice_shows_at_rest_and_goes_when_recording_starts()
    {
        var bar = new FlowBarWindow();
        bar.Show();
        var idle = bar.Width;

        bar.ShowNotice("Azure unreachable — used Parakeet", alert: false);

        bar.State.ShouldBe(BarState.Idle);
        bar.Label.ShouldBe("Azure unreachable — used Parakeet");
        bar.Width.ShouldBeGreaterThan(idle);
        bar.Width.ShouldBeLessThanOrEqualTo(Tokens.Bar.NoticeMaxWidth);
        AccentPolicy.IsRed(bar.Dot.DotColor).ShouldBeFalse();
        bar.Dot.DotColor.ShouldBe(Tokens.Colors.BarDotIdle, "a fallback is news, not an alarm");

        bar.ApplyState(BarState.Listening);
        bar.Notice.ShouldBeNull();
        bar.ApplyState(BarState.Idle);
        bar.Label.ShouldBeEmpty();
        bar.Width.ShouldBe(idle);
    }

    [AvaloniaFact]
    public void A_lost_dictation_lights_the_notice_colour_not_red()
    {
        var bar = new FlowBarWindow();
        bar.Show();

        bar.ShowNotice(BarNotices.ForFault(new TranscriptionException(TranscriptionFailure.Unauthorized, "401")), alert: true);

        bar.Label.ShouldBe("Key rejected — nothing typed");
        bar.Dot.DotColor.ShouldBe(Tokens.Colors.BarNotice);
        AccentPolicy.IsRed(bar.Dot.DotColor).ShouldBeFalse();

        bar.ClearNotice(sync: true);
        bar.Label.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task The_bar_menu_copies_the_last_transcript()
    {
        using var settings = Settings();
        var composition = Preview(settings);
        var bar = new FlowBarWindow(composition);
        bar.Show();

        (await bar.CopyLastAsync()).ShouldBeFalse("nothing dictated yet");

        composition.PasteLast.Remember("copy me");
        (await bar.CopyLastAsync()).ShouldBeTrue();
        (await bar.Clipboard!.TryGetTextAsync()).ShouldBe("copy me");

        var items = ((IEnumerable<object>)bar.ContextMenu!.ItemsSource!).OfType<MenuItem>().Select(m => m.Header as string);
        items.ShouldContain("Copy last transcript");
    }

    // ---- Settings ----

    [AvaloniaFact]
    public void Settings_records_the_paste_last_shortcut_and_registers_it()
    {
        using var settings = Settings();
        var hotkeys = new FakeGlobalHotkeys();
        var composition = Preview(settings, hotkeys: hotkeys);
        settings.Data.PasteLastHotkey.ShouldBeNull("no shortcut is taken from other apps until the user picks one");
        hotkeys.Registered.ContainsKey(PasteLastTranscript.HotkeyId).ShouldBeFalse();

        var view = Show(new SettingsView(settings, pasteLast: composition.PasteLast));
        var recorder = view.PasteLastRecorder.ShouldNotBeNull();
        recorder.Chord.ShouldBeNull();
        view.PasteLastStatus.ShouldBeEmpty("nothing chosen, so there is nothing to explain");

        recorder.Accept(Key.P, KeyModifiers.Control | KeyModifiers.Shift);

        settings.Data.PasteLastHotkey.ShouldBe(HotkeyChords.Parse("Ctrl+Shift+P"));
        hotkeys.Registered[PasteLastTranscript.HotkeyId].ShouldBe(HotkeyChords.Parse("Ctrl+Shift+P"));

        hotkeys.Taken.Add(HotkeyChords.Parse("Ctrl+Alt+B"));
        recorder.Accept(Key.B, KeyModifiers.Control | KeyModifiers.Alt);
        view.PasteLastStatus.ShouldContain("Another app or Windows already uses this shortcut");
    }

    [AvaloniaFact]
    public void Settings_has_the_hands_free_switch_on_by_default()
    {
        using var settings = Settings();
        var view = Show(new SettingsView(settings));

        settings.Data.HandsFreeDoubleTap.ShouldBeTrue();
        view.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.Text == "Hands-free").ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Choosing_hindi_on_parakeet_warns_that_it_is_english_only()
    {
        using var settings = Settings();
        var view = Show(new SettingsView(settings));
        view.LanguageWarning.ShouldBeEmpty();

        settings.Update(settings.Data with { Language = LanguageMode.Hindi });
        Dispatcher.UIThread.RunJobs();
        view.LanguageWarning.ShouldContain("English only");

        settings.Update(settings.Data with { Engine = EngineMode.Azure });
        Dispatcher.UIThread.RunJobs();
        view.LanguageWarning.ShouldBeEmpty("Azure understands Hindi");
    }

    // ---- The cloud request ----

    [Theory]
    [InlineData(LanguageMode.Auto, null, false)]
    [InlineData(LanguageMode.English, "en", false)]
    [InlineData(LanguageMode.Hindi, "hi", false)]
    [InlineData(LanguageMode.Hinglish, null, true)]
    public async Task The_language_reaches_the_azure_request(LanguageMode mode, string? code, bool hint)
    {
        var data = new SettingsData
        {
            AzureEndpoint = "https://contoso.openai.azure.com",
            AzureDeploymentAccurate = "gpt-4o-transcribe",
            Language = mode,
        };

        var options = AzureProfiles.Resolve(data).ShouldNotBeNull();
        var handler = new StubHttpMessageHandler().RespondText("namaste");
        using var http = new HttpClient(handler);
        var client = new AzureTranscriptionClient(http);

        await client.TranscribeAsync(options, "key-1234", new float[1600], ["Murmur"],
            cancellationToken: TestContext.Current.CancellationToken);

        var request = handler.Requests.ShouldHaveSingleItem();
        if (code is null) request.Part("language").ShouldBeNull();
        else request.Part("language").ShouldNotBeNull().Text.ShouldBe(code);

        var prompt = request.Part("prompt").ShouldNotBeNull().Text;
        prompt.ShouldContain("Murmur", Case.Sensitive, "the dictionary terms still go");
        prompt.Contains("romanised Hindi", StringComparison.Ordinal).ShouldBe(hint);
    }

    // ---- History ----

    [AvaloniaFact]
    public async Task A_row_offers_retry_with_the_other_engine_and_the_result_replaces_it()
    {
        using var settings = Settings();
        var store = new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl"));
        var heard = new TranscriptRecord { At = DateTimeOffset.Now, AudioSeconds = 1, Text = "heard by azure", Engine = TranscriberKind.Azure };
        store.Add(heard);

        var parakeet = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("heard by parakeet");
        var composition = Preview(settings, store, engines: kind => kind == TranscriberKind.Parakeet ? parakeet : null);
        composition.Audio.Add(heard.Id, new float[AudioChunk.SampleRate]);

        var view = Show(new TranscriptionsView(store, composition: composition));
        Dispatcher.UIThread.RunJobs();

        var retry = view.GetVisualDescendants().OfType<SoftButton>().Single(b => b.Label == "Retry with Parakeet");
        retry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        await DictationEngineWait.UntilAsync(() => store.Records[0].Text == "heard by parakeet");
        Dispatcher.UIThread.RunJobs();

        var record = store.Records.ShouldHaveSingleItem();
        record.RetriedFrom.ShouldBe(heard.Id);
        record.Engine.ShouldBe(TranscriberKind.Parakeet);
        TranscriptionsView.EngineCaption(record).ShouldBe("On this PC · retried");
        composition.PasteLast.Text.ShouldBe("heard by parakeet", "the newest transcript is what paste-last pastes");
    }

    [AvaloniaFact]
    public void Without_audio_or_an_allowed_engine_there_is_no_retry_and_deleting_forgets_the_audio()
    {
        using var settings = Settings();
        var store = new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl"));
        var old = new TranscriptRecord { At = DateTimeOffset.Now, Text = "no audio kept", Engine = TranscriberKind.Azure };
        var kept = new TranscriptRecord { At = DateTimeOffset.Now, Text = "audio kept", Engine = TranscriberKind.Azure };
        store.Add(old);
        store.Add(kept);

        var composition = Preview(settings, store, engines: _ => null);
        composition.Audio.Add(kept.Id, new float[160]);

        var view = Show(new TranscriptionsView(store, composition: composition));
        view.GetVisualDescendants().OfType<SoftButton>().Any(b => b.Label.StartsWith("Retry", StringComparison.Ordinal))
            .ShouldBeFalse("no engine may be used");
        view.GetVisualDescendants().OfType<SoftButton>().Count(b => b.Label == "Paste").ShouldBe(2);

        var delete = view.GetVisualDescendants().OfType<SoftButton>().First(b => b.Label == "Delete");
        delete.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        composition.Audio.Contains(kept.Id).ShouldBeFalse();
    }

    [Fact]
    public void Rows_say_which_engine_heard_them()
    {
        TranscriptionsView.EngineCaption(new TranscriptRecord()).ShouldBeNull();
        TranscriptionsView.EngineCaption(new TranscriptRecord { Engine = TranscriberKind.Parakeet }).ShouldBe("On this PC");
        TranscriptionsView.EngineCaption(new TranscriptRecord { Engine = TranscriberKind.Azure }).ShouldBe("Azure");
        TranscriptionsView.EngineCaption(new TranscriptRecord { Engine = TranscriberKind.Parakeet, FellBack = true })
            .ShouldBe("Offline fallback");
    }

    // ---- Home ----

    [AvaloniaFact]
    public void The_dashboard_shows_the_usage_and_follows_the_prices()
    {
        using var settings = Settings();
        var store = new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl"));
        store.Add(new TranscriptRecord
        {
            At = DateTimeOffset.Now,
            AudioSeconds = 120,
            Text = string.Join(' ', Enumerable.Repeat("word", 200)),
            Engine = TranscriberKind.Azure,
            Profile = CloudProfile.Accurate,
        });

        var dashboard = Show(new UsageDashboard(store, settings));

        dashboard.Summary.Words.ShouldBe(200);
        dashboard.Summary.EstimatedCloudCost.ShouldBe(0.012, 1e-9);
        dashboard.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "$0.01").ShouldBeTrue();

        settings.Update(settings.Data with { CloudPricePerMinuteAccurate = 0.5 });
        Dispatcher.UIThread.RunJobs();
        dashboard.Summary.EstimatedCloudCost.ShouldBe(1.0, 1e-9);
        dashboard.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "$1.00").ShouldBeTrue();
    }
}

/// <summary>Waits on the UI thread without blocking it.</summary>
internal static class DictationEngineWait
{
    public static async Task UntilAsync(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5).ConfigureAwait(true);
        }
    }
}
