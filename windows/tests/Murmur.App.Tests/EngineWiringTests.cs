using System.Net;
using Murmur.Abstractions;
using Murmur.App;
using Murmur.Cloud;
using Murmur.Core;
using Murmur.Speech;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.AppTests;

/// <summary>How the app wires the cloud engine into the engine slot.</summary>
public sealed class EngineWiringTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"murmur-engine-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        condition().ShouldBeTrue(because);
    }

    /// <summary>Holds the key down until audio has arrived, then lets go and waits for the result.</summary>
    /// <remarks>
    /// Audio is noticed through the engine's event rather than by polling the level: the tone
    /// is short, and on a loaded machine the level can rise and fall back to zero between polls.
    /// </remarks>
    private static async Task SpeakAsync(DictationEngine engine, FakeHotkeySource hotkey)
    {
        var heard = 0;
        void OnChanged(object? sender, EventArgs e)
        {
            if (engine.Level > 0) Volatile.Write(ref heard, 1);
        }

        engine.Changed += OnChanged;
        try
        {
            hotkey.Press();
            await WaitForAsync(() => Volatile.Read(ref heard) == 1, "audio should arrive");
        }
        finally
        {
            engine.Changed -= OnChanged;
        }

        hotkey.Release();
        await WaitForAsync(() => engine.State == DictationState.Idle, "the dictation finishes");
    }

    /// <summary>A cloud engine with nothing set up behind it, over a handler that refuses everything.</summary>
    private static AzureOpenAITranscriber NotSetUp(StubHttpMessageHandler? handler = null) =>
        new(new AzureTranscriptionClient(new HttpClient(handler ?? new StubHttpMessageHandler())), () => null, () => null);

    private static SettingsData Configured(SettingsData data) => data with
    {
        Engine = EngineMode.AzureWithParakeetFallback,
        AzureEndpoint = "https://contoso.openai.azure.com/",
        AzureDeploymentAccurate = "gpt-4o-transcribe",
        AzureDeploymentFast = "gpt-4o-mini-transcribe",
    };

    [Fact]
    public async Task A_cloud_engine_that_is_not_set_up_says_so_rather_than_answering()
    {
        var handler = new StubHttpMessageHandler();
        await using var cloud = NotSetUp(handler);

        cloud.Kind.ShouldBe(TranscriberKind.Azure);
        cloud.IsReady.ShouldBeFalse();
        (await cloud.LoadAsync(CancellationToken.None)).ShouldBeFalse();

        var failure = await Should.ThrowAsync<TranscriptionException>(
            () => cloud.TranscribeAsync(new float[16], [], CancellationToken.None).AsTask());
        failure.Failure.ShouldBe(TranscriptionFailure.NotConfigured);
        failure.IsTransient.ShouldBeFalse("fallback mode must report this, not hide it behind Parakeet");
        handler.Requests.ShouldBeEmpty("nothing is sent without an endpoint, a deployment and a key");
    }

    [Fact]
    public async Task Fallback_mode_without_a_cloud_setup_reports_it_and_does_not_use_Parakeet()
    {
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("local");
        await using var router = new RoutingTranscriber(
            () => EngineMode.AzureWithParakeetFallback, local, NotSetUp());

        (await Should.ThrowAsync<TranscriptionException>(
                () => router.TranscribeAsync(new float[16], [], CancellationToken.None).AsTask()))
            .Failure.ShouldBe(TranscriptionFailure.NotConfigured);
        local.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Fallback_mode_answers_with_Parakeet_when_Azure_is_down()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        settings.Update(Configured(settings.Data));

        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.ServiceUnavailable);
        var client = new AzureTranscriptionClient(
            new HttpClient(handler), delay: (_, _) => Task.CompletedTask);
        var azure = new AzureOpenAITranscriber(client, () => AzureProfiles.Resolve(settings.Data), () => "secret-key");
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("heard locally");

        await using var router = new RoutingTranscriber(() => settings.Data.Engine, local, azure);

        (await router.TranscribeAsync(new float[1600], [], CancellationToken.None)).ShouldBe("heard locally");
        router.LastUsedKind.ShouldBe(TranscriberKind.Parakeet);
        router.LastFellBack.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(AzureTranscriptionClient.DefaultMaxAttempts, "503 is retried before giving up");
    }

    [Fact]
    public async Task Fallback_mode_does_not_hide_a_rejected_key_behind_Parakeet()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        settings.Update(Configured(settings.Data));

        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.Unauthorized);
        var azure = new AzureOpenAITranscriber(
            new AzureTranscriptionClient(new HttpClient(handler)), () => AzureProfiles.Resolve(settings.Data), () => "bad-key");
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("heard locally");

        await using var router = new RoutingTranscriber(() => settings.Data.Engine, local, azure);

        (await Should.ThrowAsync<TranscriptionException>(
                () => router.TranscribeAsync(new float[1600], [], CancellationToken.None).AsTask()))
            .Failure.ShouldBe(TranscriptionFailure.Unauthorized);
        local.Calls.ShouldBe(0);
        handler.Requests.Count.ShouldBe(1, "a 401 is never retried");
    }

    [Fact]
    public async Task The_Accurate_and_Fast_switch_applies_to_the_next_request()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        settings.Update(Configured(settings.Data) with { Engine = EngineMode.Azure });

        var handler = new StubHttpMessageHandler().RespondText("hello");
        var azure = new AzureOpenAITranscriber(
            new AzureTranscriptionClient(new HttpClient(handler)), () => AzureProfiles.Resolve(settings.Data), () => "k");

        await azure.TranscribeAsync(new float[1600], [], CancellationToken.None);
        AzureProfiles.Select(settings, CloudProfile.Fast);
        await azure.TranscribeAsync(new float[1600], [], CancellationToken.None);

        handler.Requests[0].Uri!.AbsolutePath.ShouldContain("/deployments/gpt-4o-transcribe/");
        handler.Requests[1].Uri!.AbsolutePath.ShouldContain("/deployments/gpt-4o-mini-transcribe/");
    }

    [Fact]
    public async Task Switching_to_Parakeet_loads_it_at_once_and_switching_away_does_not()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        settings.Update(settings.Data with { Engine = EngineMode.Azure });

        var local = new ScriptedTranscriber(TranscriberKind.Parakeet);
        await using var router = new RoutingTranscriber(
            () => settings.Data.Engine, local, NotSetUp());

        Composition.ApplyLive(
            settings, new ModelLocator(() => null, () => [], _ => false), engine: null, router, cancellationToken: TestContext.Current.CancellationToken);

        settings.Update(settings.Data with { Engine = EngineMode.AzureWithParakeetFallback });
        await Task.Delay(50, cancellationToken: TestContext.Current.CancellationToken);
        local.LoadCalls.ShouldBe(0, "fallback mode loads Parakeet lazily, not on the switch");

        settings.Update(settings.Data with { Engine = EngineMode.Parakeet });
        await WaitForAsync(() => local.LoadCalls == 1, "Parakeet mode loads up front, as at startup");
    }

    [Fact]
    public async Task A_401_during_dictation_marks_the_key_rejected_and_a_cloud_success_clears_it()
    {
        var hotkey = new FakeHotkeySource();
        var transcriber = new ScriptedTranscriber(TranscriberKind.Azure)
            .Throws(new TranscriptionException(TranscriptionFailure.Unauthorized, "rejected", 401))
            .Returns("hello");
        var key = new CloudKey(new InMemorySecretStore());
        key.Save("some-key");

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(0.5), hotkey, transcriber, new RecordingTextInjector(), () => []);
        // The scripted engine stands in for Azure, so the key it "sent" is read here instead.
        var use = new DictationKeyUse(key);
        use.Read().ShouldBe("some-key");
        Composition.TrackKeyRejection(engine, key, use);
        engine.Start();

        await SpeakAsync(engine, hotkey);
        await WaitForAsync(() => key.State == CloudKeyState.Rejected, "a 401 is the key, not the network");

        await SpeakAsync(engine, hotkey);
        await WaitForAsync(() => key.State == CloudKeyState.Saved, "a cloud dictation that worked clears it");
    }

    [Fact]
    public async Task A_403_is_the_resource_network_rules_and_leaves_the_key_alone()
    {
        var hotkey = new FakeHotkeySource();
        var transcriber = new ScriptedTranscriber(TranscriberKind.Azure)
            .Throws(new TranscriptionException(TranscriptionFailure.Forbidden, "network rules", 403));
        var key = new CloudKey(new InMemorySecretStore());
        key.Save("good-key");
        var use = new DictationKeyUse(key);
        use.Read().ShouldBe("good-key");

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(0.5), hotkey, transcriber, new RecordingTextInjector(), () => []);
        // Subscribed after the tracker, so it has run by the time this counts the fault.
        Composition.TrackKeyRejection(engine, key, use);
        var faults = 0;
        engine.TranscriptionFaulted += (_, _) => Interlocked.Increment(ref faults);
        engine.Start();

        await SpeakAsync(engine, hotkey);
        await WaitForAsync(() => Volatile.Read(ref faults) == 1, "the 403 is reported");

        key.State.ShouldBe(CloudKeyState.Saved, "a valid key shut out by network rules is not a rejected key");
        CloudServices.IsKeyRejection(TranscriptionFailure.Forbidden).ShouldBeFalse();
        CloudServices.IsKeyRejection(TranscriptionFailure.Unauthorized).ShouldBeTrue();
    }

    [Fact]
    public async Task A_401_for_a_key_replaced_during_the_dictation_does_not_reject_the_new_one()
    {
        var hotkey = new FakeHotkeySource();
        var transcriber = new ScriptedTranscriber(TranscriberKind.Azure)
            .Throws(new TranscriptionException(TranscriptionFailure.Unauthorized, "rejected", 401));
        var key = new CloudKey(new InMemorySecretStore());
        key.Save("old-key");
        var use = new DictationKeyUse(key);

        await using var engine = new DictationEngine(
            FakeAudioCapture.Tone(0.5), hotkey, transcriber, new RecordingTextInjector(), () => []);
        Composition.TrackKeyRejection(engine, key, use);
        engine.Start();

        // The request went out with the old key; the user then saved the right one.
        use.Read().ShouldBe("old-key");
        key.Save("new-key");

        await SpeakAsync(engine, hotkey);
        transcriber.Calls.ShouldBe(1);

        key.State.ShouldBe(CloudKeyState.Saved);
    }

    [Fact]
    public async Task A_connection_test_that_returns_after_a_new_key_is_saved_does_not_reject_it()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        settings.Update(Configured(settings.Data));

        CloudKey? key = null;
        var handler = new StubHttpMessageHandler().Then((_, _) =>
        {
            key!.Save("the-right-key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        await using var composition = Composition.ForPreview(
            settings,
            new DictionaryFile(Path.Combine(_directory, "dictionary.jsonl")),
            new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl")),
            cloudHandler: handler);
        key = composition.Cloud.Key;
        key.Save("the-wrong-key");

        (await composition.Cloud.TestAsync(CloudProfile.Accurate, cancellationToken: TestContext.Current.CancellationToken)).Failure.ShouldBe(TranscriptionFailure.Unauthorized);
        key.State.ShouldBe(CloudKeyState.Saved, "the 401 was for the key that has since been replaced");
    }

    [Fact]
    public void Parakeet_mode_keeps_the_plain_budget_and_the_cloud_modes_follow_the_timeout_live()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var limits = Composition.LimitsFor(settings);
        var audio = TimeSpan.FromSeconds(2);

        settings.Update(settings.Data with { Engine = EngineMode.Parakeet, CloudRequestTimeoutSeconds = 30 });
        limits.TranscriptionBudget(audio).ShouldBe(TimeSpan.FromSeconds(10 + 4), "10 s + 2x the audio, as before");

        settings.Update(settings.Data with { Engine = EngineMode.AzureWithParakeetFallback });
        limits.TranscriptionBudget(audio).ShouldBe(TimeSpan.FromSeconds(30 + 10 + 4), "room for the cloud and then Parakeet");

        settings.Update(settings.Data with { CloudRequestTimeoutSeconds = 60 });
        limits.TranscriptionBudget(audio).ShouldBe(TimeSpan.FromSeconds(60 + 10 + 4), "a changed timeout needs no restart");
    }

    [Fact]
    public async Task Parakeet_mode_never_reads_the_key_store_for_readiness()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var secrets = new InMemorySecretStore();
        await using var composition = Composition.ForPreview(
            settings,
            new DictionaryFile(Path.Combine(_directory, "dictionary.jsonl")),
            new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl")),
            secrets: secrets);

        for (var i = 0; i < 3; i++) _ = composition.Readiness;
        secrets.Lookups.ShouldBe(0);
    }

    [Fact]
    public async Task Readiness_follows_the_mode_the_setup_and_the_key()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var secrets = new InMemorySecretStore();
        await using var composition = Composition.ForPreview(
            settings,
            new DictionaryFile(Path.Combine(_directory, "dictionary.jsonl")),
            new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl")),
            secrets: secrets);

        var raised = 0;
        composition.ReadinessChanged += (_, _) => raised++;

        settings.Update(settings.Data with { Engine = EngineMode.Azure });
        composition.Readiness.ShouldBe(EngineReadiness.NotConfigured);

        settings.Update(Configured(settings.Data) with { Engine = EngineMode.Azure });
        composition.Readiness.ShouldBe(EngineReadiness.NoApiKey);

        composition.Cloud.Key.Save("abcd1234");
        composition.Readiness.ShouldBe(EngineReadiness.Ready);

        composition.Cloud.Key.MarkRejected();
        composition.Readiness.ShouldBe(EngineReadiness.KeyRejected);
        EngineStatus.ShortLabel(composition.Readiness).ShouldBe("Key rejected");

        composition.Cloud.Key.Save("efgh5678");
        composition.Readiness.ShouldBe(EngineReadiness.Ready, "saving a new key clears the rejection");

        raised.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task A_rejected_key_in_the_connection_test_marks_the_key_rejected()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        settings.Update(Configured(settings.Data));

        var handler = new StubHttpMessageHandler().Respond(HttpStatusCode.Unauthorized).RespondText("");
        await using var composition = Composition.ForPreview(
            settings,
            new DictionaryFile(Path.Combine(_directory, "dictionary.jsonl")),
            new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl")),
            cloudHandler: handler);
        composition.Cloud.Key.Save("rejected-key");

        var rejected = await composition.Cloud.TestAsync(CloudProfile.Accurate, cancellationToken: TestContext.Current.CancellationToken);
        rejected.Succeeded.ShouldBeFalse();
        rejected.Failure.ShouldBe(TranscriptionFailure.Unauthorized);
        composition.Cloud.Key.State.ShouldBe(CloudKeyState.Rejected);

        var accepted = await composition.Cloud.TestAsync(CloudProfile.Fast, cancellationToken: TestContext.Current.CancellationToken);
        accepted.Succeeded.ShouldBeTrue();
        composition.Cloud.Key.State.ShouldBe(CloudKeyState.Saved, "a request that gets through clears it");
        composition.Cloud.ConfiguredProfiles().ShouldBe([CloudProfile.Accurate, CloudProfile.Fast]);
    }

    [Fact]
    public async Task The_connection_test_explains_what_is_missing_without_sending_anything()
    {
        using var settings = new AppSettings(Path.Combine(_directory, "settings.json"), new ManualTimeProvider());
        var handler = new StubHttpMessageHandler();
        await using var composition = Composition.ForPreview(
            settings,
            new DictionaryFile(Path.Combine(_directory, "dictionary.jsonl")),
            new TranscriptStore(Path.Combine(_directory, "transcripts.jsonl")),
            cloudHandler: handler);

        (await composition.Cloud.TestAsync(CloudProfile.Accurate, cancellationToken: TestContext.Current.CancellationToken)).Message.ShouldContain("endpoint");

        settings.Update(Configured(settings.Data));
        (await composition.Cloud.TestAsync(CloudProfile.Accurate, cancellationToken: TestContext.Current.CancellationToken)).Message.ShouldContain("API key");

        handler.Requests.ShouldBeEmpty();
    }
}
