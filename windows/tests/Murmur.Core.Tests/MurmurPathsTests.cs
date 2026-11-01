using Murmur.Core;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>Setting the environment variable changes the process for every test running alongside.</summary>
[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

/// <summary>
/// The data folder and its override, which lets the published exe be measured with settings of
/// its own without touching the real user's.
/// </summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class MurmurPathsTests
{
    private static readonly string LocalAppData = Path.Combine(Path.GetTempPath(), "local-app-data");

    [Fact]
    public void Without_an_override_the_folder_is_Murmur_under_local_app_data()
    {
        MurmurPaths.Resolve(null, LocalAppData).ShouldBe(Path.Combine(LocalAppData, "Murmur"));
        MurmurPaths.Resolve("", LocalAppData).ShouldBe(Path.Combine(LocalAppData, "Murmur"));
        MurmurPaths.Resolve("   ", LocalAppData).ShouldBe(Path.Combine(LocalAppData, "Murmur"));
    }

    [Fact]
    public void An_override_is_used_as_is_and_made_absolute()
    {
        var folder = Path.Combine(Path.GetTempPath(), "murmur-measure");

        MurmurPaths.Resolve(folder, LocalAppData).ShouldBe(folder);
        MurmurPaths.Resolve($"  {folder}  ", LocalAppData).ShouldBe(folder);
        Path.IsPathFullyQualified(MurmurPaths.Resolve("relative-folder", LocalAppData)).ShouldBeTrue();
    }

    [Fact]
    public void Every_store_and_log_follows_the_variable()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"murmur-data-{Guid.NewGuid():N}");
        var before = Environment.GetEnvironmentVariable(MurmurPaths.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(MurmurPaths.OverrideVariable, folder);

            MurmurPaths.DataDirectory.ShouldBe(folder);
            AppSettings.DefaultPath.ShouldBe(Path.Combine(folder, "settings.json"));
            DictionaryFile.DefaultPath.ShouldBe(Path.Combine(folder, "dictionary.txt"));
            SnippetStore.DefaultPath.ShouldBe(Path.Combine(folder, "snippets.jsonl"));
            TranscriptStore.DefaultPath.ShouldBe(Path.Combine(folder, "transcripts.jsonl"));
            ProtectedSecretFile.DefaultDirectory.ShouldBe(Path.Combine(folder, "secrets"));
            RecentAudio.DefaultDirectory.ShouldBe(Path.Combine(folder, "recent-audio"));

            Environment.SetEnvironmentVariable(MurmurPaths.OverrideVariable, null);

            MurmurPaths.DataDirectory.ShouldBe(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Murmur"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(MurmurPaths.OverrideVariable, before);
        }
    }
}
