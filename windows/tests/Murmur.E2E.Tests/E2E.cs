using System.Globalization;
using Murmur.Cloud;
using Murmur.Core;
using Murmur.Speech;
using Xunit;

// One dictation at a time: the suite measures latency and memory, and a second test's model
// load or decode running alongside would make every number meaningless.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Murmur.E2E.Tests;

/// <summary>Whether the suite runs, and where its inputs are.</summary>
/// <remarks>
/// <para>
/// Every test starts with <see cref="Require"/>. Without <c>MURMUR_E2E=1</c> it skips, so CI and an
/// ordinary <c>dotnet test Murmur.sln</c> run nothing here and need neither the model nor the clips.
/// With it set, a missing model or clip folder is a failure, not a skip: someone asked for the
/// suite and should be told why it could not run.
/// </para>
/// <para>
/// The first test to run points <see cref="MurmurPaths.OverrideVariable"/> at a scratch folder for
/// the rest of the process, so the engine's own logs land there rather than in the real
/// <c>%LocalAppData%\Murmur</c>.
/// </para>
/// </remarks>
internal static class E2E
{
    /// <summary>The variable that turns the suite on.</summary>
    public const string EnableVariable = "MURMUR_E2E";

    /// <summary>The variable naming the folder of spoken clips.</summary>
    public const string AudioVariable = "MURMUR_E2E_AUDIO";

    private static readonly Lock InitLock = new();
    private static string? _scratch;

    /// <summary>Whether <c>MURMUR_E2E=1</c>.</summary>
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal);

    /// <summary>The folder holding the clips and <c>expected.tsv</c>. It has no default.</summary>
    public static string AudioDirectory =>
        Environment.GetEnvironmentVariable(AudioVariable) is { Length: > 0 } configured
            ? configured
            : throw new InvalidOperationException($"Set {AudioVariable} to the folder of spoken clips.");

    /// <summary>The installed Parakeet model.</summary>
    public static string ModelDirectory =>
        ParakeetTranscriber.Locate()
        ?? throw new InvalidOperationException(
            "The Parakeet model is not installed under %LOCALAPPDATA%\\Murmur\\models\\parakeet-v2. "
            + "See docs/PARAKEET-WINDOWS.md.");

    /// <summary>A process-wide scratch folder, removed by nobody: it is under the temp folder.</summary>
    public static string Scratch
    {
        get
        {
            lock (InitLock)
            {
                if (_scratch is not null) return _scratch;

                _scratch = Path.Combine(
                    Path.GetTempPath(),
                    "murmur-e2e",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(_scratch);

                // Logs from the engine and anything else that asks for the data folder.
                var data = Path.Combine(_scratch, "data");
                Directory.CreateDirectory(data);
                Environment.SetEnvironmentVariable(MurmurPaths.OverrideVariable, data);
                return _scratch;
            }
        }
    }

    /// <summary>Where measurements are written for the report: <c>windows/artifacts/e2e</c>.</summary>
    public static string ArtifactsDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Murmur.sln")))
            {
                directory = directory.Parent;
            }

            var root = directory?.FullName ?? Scratch;
            var path = Path.Combine(root, "artifacts", "e2e");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>Skips the test unless the suite is switched on, and checks its inputs when it is.</summary>
    public static void Require()
    {
        Assert.SkipUnless(Enabled, $"End-to-end tests run only with {EnableVariable}=1 (see docs/TESTING-LOCAL.md).");

        if (Environment.GetEnvironmentVariable(AudioVariable) is not { Length: > 0 })
        {
            Assert.Fail($"{AudioVariable} is not set. Set it to the folder of spoken clips (see docs/TESTING-LOCAL.md).");
        }

        if (!File.Exists(Path.Combine(AudioDirectory, "expected.tsv")))
        {
            Assert.Fail($"No expected.tsv in {AudioDirectory}. Set {AudioVariable} to the folder of spoken clips.");
        }

        _ = ModelDirectory;
        _ = Scratch;

        // The stand-in for Azure is plain http on 127.0.0.1. The app itself refuses that; only
        // this process allows it.
        AzureEndpoint.AllowLoopbackHttp = true;
    }

    /// <summary>Writes a line to the test's output and to a file under <see cref="ArtifactsDirectory"/>.</summary>
    /// <param name="file">The file name, such as <c>latency.tsv</c>.</param>
    /// <param name="line">The line.</param>
    public static void Report(string file, string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        lock (InitLock)
        {
            File.AppendAllText(Path.Combine(ArtifactsDirectory, file), line + Environment.NewLine);
        }
    }
}
