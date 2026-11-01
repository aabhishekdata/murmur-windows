using System.Diagnostics;
using System.Globalization;
using Murmur.Core;
using Murmur.Platform.Windows;
using Shouldly;
using Xunit;

namespace Murmur.E2E.Tests;

/// <summary>
/// The published single-file exe itself: its self-test, and what it costs to leave running.
/// </summary>
/// <remarks>
/// <para>
/// Needs <c>MURMUR_E2E_EXE</c> pointing at an exe published the way CI publishes it (see
/// docs/TESTING-LOCAL.md), and skips without it. Every run gets its own data folder through
/// <see cref="MurmurPaths.OverrideVariable"/>, so the real user's settings and key on this PC are
/// never read or written; only the model is shared, from <c>%LocalAppData%\Murmur\models</c>.
/// </para>
/// <para>
/// The idle figures are for the app at rest in the tray, with the floating bar showing, after it
/// has had <see cref="Settle"/> to start up. Processor time is the process's own counter, so it
/// counts every thread, native ones included.
/// </para>
/// </remarks>
public sealed class PublishedExeTests
{
    /// <summary>The variable naming the published exe.</summary>
    public const string ExeVariable = "MURMUR_E2E_EXE";

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Measure = TimeSpan.FromSeconds(60);

    private static string RequireExe()
    {
        E2E.Require();
        var exe = Environment.GetEnvironmentVariable(ExeVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(exe), $"Set {ExeVariable} to a published Murmur.App.exe to check it.");
        File.Exists(exe).ShouldBeTrue($"{exe} does not exist");

        // A second instance exits at once (single-instance mutex), which would measure nothing. A copy
        // this suite has just killed can still be listed for a moment, so give it a few seconds to go.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (Running() > 0 && DateTime.UtcNow < deadline) Thread.Sleep(250);
        Running().ShouldBe(0, "close Murmur before running this; it is single-instance");
        return exe!;

        static int Running()
        {
            var processes = Process.GetProcessesByName("Murmur.App");
            foreach (var process in processes) process.Dispose();
            return processes.Length;
        }
    }

    [Fact]
    public async Task The_published_exe_passes_its_self_test()
    {
        var exe = RequireExe();
        var data = Path.Combine(E2E.Scratch, "selftest-data");
        Directory.CreateDirectory(data);

        using var process = Start(exe, data, "--selftest");
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancel.CancelAfter(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(cancel.Token);
        }
        finally
        {
            // A hung self-test would otherwise keep running, holding the single-instance mutex
            // and failing every later test here as "close Murmur".
            await StopAsync(process);
        }

        E2E.Report("idle.tsv", $"{DateTime.Now:s}\tselftest\texit {process.ExitCode}");
        process.ExitCode.ShouldBe(0, $"the self-test failed; see {data}");
    }

    [Fact]
    public async Task At_rest_Azure_mode_leaves_Parakeet_unloaded_and_costs_far_less_memory()
    {
        var exe = RequireExe();

        var parakeet = await MeasureAsync(exe, "A: Parakeet", data => data with { Engine = EngineMode.Parakeet }, key: null);

        // The app refuses plain http, so the exe is told the stand-in is https on the same port.
        // Nothing should be sent at rest; if anything were, its TLS handshake would still open a
        // connection to the stand-in, and be counted.
        await using var mock = await MockAzureServer.StartAsync(DictationTests.Key, Pipeline.AccurateDeployment, Pipeline.FastDeployment);
        var https = new UriBuilder(mock.Endpoint) { Scheme = Uri.UriSchemeHttps }.Uri.AbsoluteUri;
        var azure = await MeasureAsync(
            exe,
            "B: Azure",
            data => Pipeline.PointedAt(data, mock, EngineMode.Azure) with { AzureEndpoint = https },
            key: DictationTests.Key);

        parakeet.SpeechLoaded.ShouldBeTrue("Parakeet mode loads the model at startup");
        azure.SpeechLoaded.ShouldBeFalse("Azure mode must not load Parakeet, or even its native runtime");
        mock.Connections.ShouldBe(0, "an idle app sends nothing");

        // The plan's targets (Verification): under 200 MB private bytes in Azure mode with
        // Parakeet unloaded, and at most 0.5 % CPU at idle, held here to one core rather than the
        // whole machine. The measured figures are in idle.tsv.
        azure.PrivateAverage.ShouldBeLessThan(200L * 1024 * 1024, "Azure mode with Parakeet unloaded");
        azure.PrivateAverage.ShouldBeLessThan(parakeet.PrivateAverage / 2, "Azure mode should cost a fraction of Parakeet's memory");
        parakeet.CpuPercentOfOneCore.ShouldBeLessThanOrEqualTo(IdleCpuTarget);
        azure.CpuPercentOfOneCore.ShouldBeLessThanOrEqualTo(IdleCpuTarget);
    }

    /// <summary>The plan's idle CPU target, as a percentage of one core.</summary>
    private const double IdleCpuTarget = 0.5;

    private sealed record Idle(
        string Name,
        double CpuPercentOfOneCore,
        double CpuPercentOfMachine,
        long PrivateAverage,
        long PrivateMax,
        long WorkingSetAverage,
        int Threads,
        bool SpeechLoaded);

    private static async Task<Idle> MeasureAsync(
        string exe,
        string name,
        Func<SettingsData, SettingsData> configure,
        string? key)
    {
        var data = Path.Combine(E2E.Scratch, $"measure-{name[0]}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(data);

        // Written the way the app writes them: settings.json and, for B, the DPAPI-encrypted key.
        // An explicit SettingsVersion, so the first-run setup window is not shown.
        using (var settings = new AppSettings(Path.Combine(data, "settings.json")))
        {
            settings.Update(configure(settings.Data with { SettingsVersion = SettingsData.CurrentVersion }));
            settings.Flush();
        }

        if (key is not null) new CloudKey(new ProtectedSecretFile(Path.Combine(data, "secrets"), new DpapiDataProtector())).Save(key);

        using var process = Start(exe, data);
        try
        {
            var token = TestContext.Current.CancellationToken;
            await Task.Delay(Settle, token);
            process.HasExited.ShouldBeFalse($"{name}: the app exited during start-up (code {(process.HasExited ? process.ExitCode : 0)})");

            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            var privateBytes = new List<long>();
            var workingSet = new List<long>();

            using var sampler = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (clock.Elapsed < Measure && await sampler.WaitForNextTickAsync(token))
            {
                process.Refresh();
                privateBytes.Add(process.PrivateMemorySize64);
                workingSet.Add(process.WorkingSet64);
            }

            process.Refresh();
            var cpu = process.TotalProcessorTime - cpuBefore;
            var wall = clock.Elapsed;

            // sherpa-onnx's native library, and the ONNX Runtime it bundles, are loaded on the
            // first call into them — which only loading the model makes.
            var speech = process.Modules.Cast<ProcessModule>()
                .Any(m => m.ModuleName.Contains("sherpa-onnx", StringComparison.OrdinalIgnoreCase)
                          || m.ModuleName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase));

            var idle = new Idle(
                name,
                100 * cpu / wall,
                100 * cpu / wall / Environment.ProcessorCount,
                (long)privateBytes.Average(),
                privateBytes.Max(),
                (long)workingSet.Average(),
                process.Threads.Count,
                speech);

            E2E.Report(
                "idle.tsv",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTime.Now:s}\t{name}\tCPU {idle.CpuPercentOfOneCore:0.000}% of one core ({idle.CpuPercentOfMachine:0.0000}% of {Environment.ProcessorCount} logical)"
                    + $"\tprocessor time {cpu.TotalMilliseconds:0} ms in {wall.TotalSeconds:0} s"
                    + $"\tprivate avg {idle.PrivateAverage / 1048576.0:0} MB, max {idle.PrivateMax / 1048576.0:0} MB"
                    + $"\tworking set avg {idle.WorkingSetAverage / 1048576.0:0} MB\tthreads {idle.Threads}"
                    + $"\tspeech runtime loaded: {(speech ? "yes" : "no")}"));

            var crash = Path.Combine(data, "last-crash.log");
            if (File.Exists(crash)) E2E.Report("idle.tsv", $"{name}: last-crash.log: {File.ReadAllText(crash)}");

            return idle;
        }
        finally
        {
            await StopAsync(process);
        }
    }

    private static async Task StopAsync(Process process)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None);
    }

    private static Process Start(string exe, string dataDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment[MurmurPaths.OverrideVariable] = dataDirectory;

        return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
    }
}
