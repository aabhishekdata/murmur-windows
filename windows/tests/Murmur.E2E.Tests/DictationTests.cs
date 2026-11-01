using System.Text;
using Murmur.Abstractions;
using Murmur.App;
using Murmur.Core;
using Murmur.Platform.Windows;
using Shouldly;
using Xunit;

namespace Murmur.E2E.Tests;

/// <summary>
/// Every clip, through the whole pipeline, in Parakeet mode and in both Azure profiles.
/// </summary>
public sealed class DictationTests
{
    internal const string Key = "e2e-0123456789abcdef0123456789abcdef";

    public static TheoryData<string> Clips => [.. Clip.Names];

    [Theory]
    [MemberData(nameof(Clips))]
    public async Task Parakeet_mode_hears_each_clip_on_this_PC_and_sends_nothing(string name)
    {
        E2E.Require();
        var clip = Clip.Load(name);

        // The cloud is fully set up and reachable, key and all: Parakeet mode must still not use it.
        await using var mock = await MockAzureServer.StartAsync(Key, Pipeline.AccurateDeployment, Pipeline.FastDeployment);
        await using var pipeline = Pipeline.Create(data => Pipeline.PointedAt(data, mock, EngineMode.Parakeet), Key);

        // Cold: the model loads when the key goes down, as in the app. Then warm.
        foreach (var run in new[] { "cold", "warm" })
        {
            var dictation = await pipeline.DictateAsync(clip);

            var wer = Expect.Delivered(dictation);
            dictation.Result!.Engine.ShouldBe(TranscriberKind.Parakeet);
            dictation.Result.FellBack.ShouldBeFalse();
            Expect.Latency($"Parakeet ({run})", dictation, wer);
        }

        mock.Requests.ShouldBeEmpty("Parakeet mode sends nothing anywhere");
        pipeline.ParakeetBuilds.ShouldBe(1, "the model is loaded once and kept");
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public async Task Azure_mode_hears_each_clip_on_Accurate_then_Fast_without_loading_Parakeet(string name)
    {
        E2E.Require();
        var clip = Clip.Load(name);

        await using var mock = await MockAzureServer.StartAsync(Key, Pipeline.AccurateDeployment, Pipeline.FastDeployment);
        await using var pipeline = Pipeline.Create(data => Pipeline.PointedAt(data, mock, EngineMode.Azure), Key);

        var profiles = new[]
        {
            (CloudProfile.Accurate, Pipeline.AccurateDeployment),
            (CloudProfile.Fast, Pipeline.FastDeployment),
        };

        foreach (var (profile, deployment) in profiles)
        {
            // The tray's quick switch: applies to the next dictation, with no restart.
            AzureProfiles.Select(pipeline.Settings, profile);

            var before = mock.Requests.Count;
            var dictation = await pipeline.DictateAsync(clip);

            var wer = Expect.Delivered(dictation);
            dictation.Result!.Engine.ShouldBe(TranscriberKind.Azure);
            dictation.Result.FellBack.ShouldBeFalse();

            mock.Requests.Count.ShouldBe(before + 1, "one request per dictation, no retries");
            var request = mock.Requests[^1];
            Expect.WellFormed(request, Key, deployment);
            request.Language.ShouldBeNull("Auto lets the model detect the language");
            request.Status.ShouldBe(200);
            request.Audio!.Value.DataBytes.ShouldBe(clip.Samples.Length * 2, "the whole clip is uploaded, once");

            Expect.Latency($"Azure {AzureProfiles.Label(profile)}", dictation, wer, request.Transcription);
        }

        pipeline.ParakeetBuilds.ShouldBe(0, "Azure mode never loads Parakeet");
        pipeline.Key.State.ShouldBe(CloudKeyState.Saved, "a dictation that got through leaves the key accepted");
    }

    [Theory]
    [InlineData(LanguageMode.Hindi, "hi", false)]
    [InlineData(LanguageMode.English, "en", false)]
    [InlineData(LanguageMode.Hinglish, null, true)]
    public async Task The_language_mode_reaches_the_request(LanguageMode language, string? code, bool hint)
    {
        E2E.Require();
        var clip = Clip.Load("hello");

        await using var mock = await MockAzureServer.StartAsync(Key, Pipeline.AccurateDeployment, Pipeline.FastDeployment);
        await using var pipeline = Pipeline.Create(
            data => Pipeline.PointedAt(data, mock, EngineMode.Azure) with { Language = language }, Key);

        var dictation = await pipeline.DictateAsync(clip);

        dictation.Result.ShouldNotBeNull();
        var request = mock.Requests.ShouldHaveSingleItem();
        Expect.WellFormed(request, Key, Pipeline.AccurateDeployment);
        request.Language.ShouldBe(code);
        if (hint) request.Prompt.ShouldNotBeNull().ShouldContain(LanguageModes.HinglishHint);
        else request.Prompt.ShouldNotBeNull().ShouldNotContain("Hinglish");
    }

    [Fact]
    public async Task The_key_is_kept_encrypted_beside_the_settings_and_survives_a_restart()
    {
        E2E.Require();

        await using var mock = await MockAzureServer.StartAsync(Key, Pipeline.AccurateDeployment);
        string folder;
        await using (var pipeline = Pipeline.Create(data => Pipeline.PointedAt(data, mock, EngineMode.Azure), Key))
        {
            folder = pipeline.Folder;
            var dictation = await pipeline.DictateAsync(Clip.Load("hello"));
            dictation.Result.ShouldNotBeNull();
        }

        // Nothing readable on disk, anywhere in the folder.
        var keyBytes = Encoding.UTF8.GetBytes(Key);
        var unicodeKeyBytes = Encoding.Unicode.GetBytes(Key);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            bytes.AsSpan().IndexOf(keyBytes).ShouldBe(-1, $"{file} holds the key in the clear");
            bytes.AsSpan().IndexOf(unicodeKeyBytes).ShouldBe(-1, $"{file} holds the key in the clear");
        }

        File.Exists(Path.Combine(folder, "secrets", CloudKey.SecretName + ".bin")).ShouldBeTrue();

        // "Restart": a new store over the same folder decrypts it with DPAPI.
        var reopened = new CloudKey(new ProtectedSecretFile(Path.Combine(folder, "secrets"), new DpapiDataProtector()));
        reopened.Value.ShouldBe(Key);
        reopened.Masked.ShouldNotBeNull().ShouldNotContain(Key[..^4]);
    }
}
