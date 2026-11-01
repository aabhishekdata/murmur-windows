using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.App;
using Murmur.App.Controls;
using Murmur.App.Views;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;

namespace Murmur.AppTests;

/// <summary>The Transcription engine card, the bar's cloud states and the cloud model switch.</summary>
public sealed class CloudUiTests : IDisposable
{
    private const string Key = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "murmur-tests", Guid.NewGuid().ToString("N"));

    public CloudUiTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private Composition Preview(AppSettings settings, StubHttpMessageHandler? handler = null) =>
        Composition.ForPreview(
            settings,
            new DictionaryFile(Path.Combine(_directory, "dictionary.jsonl")),
            new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl")),
            secrets: new InMemorySecretStore(),
            cloudHandler: handler);

    private AppSettings Settings() => new(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());

    private static bool Shows(Control view, string text)
    {
        var descendants = view.GetVisualDescendants().ToList();
        return descendants.OfType<TextBlock>().Any(t => t.Text?.Contains(text, StringComparison.Ordinal) == true)
            || descendants.OfType<TextBox>().Any(t => t.Text?.Contains(text, StringComparison.Ordinal) == true);
    }

    private static T Show<T>(T view) where T : Control
    {
        var window = new Window { Width = 700, Height = 1600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    [AvaloniaFact]
    public void Settings_has_a_transcription_engine_card_with_Parakeet_the_default()
    {
        using var settings = Settings();
        var view = Show(new SettingsView(settings));

        Shows(view, "Transcription engine").ShouldBeTrue();
        Shows(view, "Offline (Parakeet)").ShouldBeTrue();
        Shows(view, "Nothing you say leaves the machine").ShouldBeTrue("Parakeet is the default");
        Shows(view, "diarization").ShouldBeTrue("the one-line note that it is not supported");

        var card = view.GetVisualDescendants().OfType<TranscriptionEngineCard>().ShouldHaveSingleItem();
        card.KeyStatus.ShouldBe("Keys cannot be stored here.", "a bare view has no key store");
    }

    [AvaloniaFact]
    public void Choosing_Azure_says_audio_will_leave_the_machine()
    {
        using var settings = Settings();
        var card = Show(new TranscriptionEngineCard(settings));

        // A segmented control, one segment per mode, clicked as a user would.
        var azure = card.ModeSelector.Segments.Single(s => Equals(s.Content is TextBlock t ? t.Text : null, "Azure"));
        azure.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        card.ModeSelector.SelectedLabel.ShouldBe("Azure");

        settings.Data.Engine.ShouldBe(EngineMode.Azure);
        Shows(card, "sent to your own Azure OpenAI resource").ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Typing_into_the_fields_saves_them_and_a_pasted_url_is_explained()
    {
        using var settings = Settings();
        var card = Show(new TranscriptionEngineCard(settings));

        card.EndpointField.Text =
            "https://contoso.openai.azure.com/openai/deployments/x/audio/transcriptions?api-version=2025-03-01-preview";
        card.FastField.Text = " gpt-4o-mini-transcribe ";
        Dispatcher.UIThread.RunJobs();

        settings.Data.AzureDeploymentFast.ShouldBe("gpt-4o-mini-transcribe");
        Shows(card, "Will use https://contoso.openai.azure.com").ShouldBeTrue();
        Shows(card, "Only the Fast deployment is filled in").ShouldBeTrue();
        AzureProfiles.EffectiveProfile(settings.Data).ShouldBe(CloudProfile.Fast);

        card.EndpointField.Text = "http://contoso.openai.azure.com";
        Dispatcher.UIThread.RunJobs();
        Shows(card, "must start with https://").ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task The_key_is_entered_once_then_shown_masked_with_replace_and_remove()
    {
        using var settings = Settings();
        await using var composition = Preview(settings);
        var card = Show(new TranscriptionEngineCard(settings, composition.Cloud));

        card.KeyStatus.ShouldBe("No key saved.");
        card.KeyField.Text = Key;
        card.SaveKey().ShouldBeTrue();
        Dispatcher.UIThread.RunJobs();

        card.KeyStatus.ShouldBe("•••• 8f90 saved");
        card.KeyField.Text.ShouldBe(string.Empty, "the field does not keep the key once it is saved");
        Shows(card, Key).ShouldBeFalse("the key is never shown again");
        settings.Data.ToString().ShouldNotContain(Key);
        card.GetVisualDescendants().OfType<SoftButton>().Select(b => b.Label)
            .ShouldContain("Replace");

        card.RemoveKey();
        Dispatcher.UIThread.RunJobs();
        card.KeyStatus.ShouldBe("No key saved.");
        composition.Cloud.Key.HasKey.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task A_key_that_cannot_be_removed_stays_saved_and_says_why()
    {
        using var settings = Settings();
        var secrets = new InMemorySecretStore();
        await using var composition = Composition.ForPreview(
            settings,
            new DictionaryFile(Path.Combine(_directory, "dictionary.jsonl")),
            new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl")),
            secrets: secrets);
        var card = Show(new TranscriptionEngineCard(settings, composition.Cloud));
        card.KeyField.Text = Key;
        card.SaveKey().ShouldBeTrue();
        Dispatcher.UIThread.RunJobs();

        secrets.RemoveFailure = new IOException("The file is in use.");
        card.RemoveKey();
        Dispatcher.UIThread.RunJobs();

        card.KeyStatus.ShouldBe("•••• 8f90 saved");
        card.KeyError.ShouldBe("The key file could not be removed.", "the refresh must not hide the error");
    }

    [AvaloniaFact]
    public void Something_that_is_not_a_key_is_refused_with_a_reason()
    {
        using var settings = Settings();
        var composition = Preview(settings);
        var card = Show(new TranscriptionEngineCard(settings, composition.Cloud));

        card.KeyField.Text = "two words";
        card.SaveKey().ShouldBeFalse();

        Shows(card, "has spaces in it").ShouldBeTrue();
        composition.Cloud.Key.HasKey.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Test_both_reports_each_deployment()
    {
        using var settings = Settings();
        settings.Update(settings.Data with
        {
            AzureEndpoint = "https://contoso.openai.azure.com",
            AzureDeploymentAccurate = "acc",
            AzureDeploymentFast = "fast",
        });

        var handler = new StubHttpMessageHandler()
            .RespondText("", ("x-ms-region", "Sweden Central"))
            .Respond(HttpStatusCode.NotFound);
        await using var composition = Preview(settings, handler);
        composition.Cloud.Key.Save(Key);
        var card = Show(new TranscriptionEngineCard(settings, composition.Cloud));

        await card.TestAsync(both: true);

        card.TestResult.ShouldContain("✓ Accurate");
        card.TestResult.ShouldContain("Sweden Central");
        card.TestResult.ShouldContain("✕ Fast");
        card.TestResult.ShouldContain("deployment name");
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].Uri!.AbsolutePath.ShouldContain("/deployments/acc/");
        handler.Requests[1].Uri!.AbsolutePath.ShouldContain("/deployments/fast/");
    }

    [AvaloniaFact]
    public async Task The_bar_asks_for_a_key_and_then_says_when_it_is_rejected()
    {
        using var settings = Settings();
        settings.Update(settings.Data with
        {
            Engine = EngineMode.Azure,
            AzureEndpoint = "https://contoso.openai.azure.com",
            AzureDeploymentAccurate = "acc",
        });

        await using var composition = Preview(settings);
        var bar = new FlowBarWindow(composition);

        bar.State.ShouldBe(BarState.Unavailable);
        bar.Label.ShouldBe("Add API key");

        composition.Cloud.Key.Save(Key);
        Dispatcher.UIThread.RunJobs();
        bar.State.ShouldBe(BarState.Idle, "Azure mode needs no local model");

        composition.Cloud.Key.MarkRejected();
        Dispatcher.UIThread.RunJobs();
        bar.State.ShouldBe(BarState.Unavailable);
        bar.Label.ShouldBe("Key rejected");

        settings.Update(settings.Data with { AzureEndpoint = null });
        Dispatcher.UIThread.RunJobs();
        bar.Label.ShouldBe("Set up Azure");
    }

    [AvaloniaFact]
    public async Task The_bar_menu_switches_between_Accurate_and_Fast()
    {
        using var settings = Settings();
        settings.Update(settings.Data with
        {
            AzureEndpoint = "https://contoso.openai.azure.com",
            AzureDeploymentAccurate = "acc",
            AzureDeploymentFast = "fast",
        });

        await using var composition = Preview(settings);
        var bar = new FlowBarWindow(composition);

        bar.RebuildCloudModelMenu();
        var items = bar.CloudModelMenu!.ItemsSource!.Cast<MenuItem>().ToList();
        items.Select(i => i.Header as string).ShouldBe(
            ["Accurate (gpt-4o-transcribe)", "Fast (gpt-4o-mini-transcribe)"]);
        items[0].Icon.ShouldNotBeNull("Accurate is the default");
        items[1].Icon.ShouldBeNull();

        items[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        settings.Data.AzureActiveProfile.ShouldBe(CloudProfile.Fast);
        AzureProfiles.Resolve(settings.Data)!.Deployment.ShouldBe("fast");

        settings.Update(settings.Data with { AzureDeploymentAccurate = null });
        bar.RebuildCloudModelMenu();
        bar.CloudModelMenu.ItemsSource!.Cast<MenuItem>().First().IsEnabled.ShouldBeFalse(
            "a profile with no deployment cannot be chosen");
    }
}
