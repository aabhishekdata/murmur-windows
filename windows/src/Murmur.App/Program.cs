using Avalonia;

namespace Murmur.App;

/// <summary>Entry point.</summary>
public static class Program
{
    /// <summary>
    /// Names the machine-wide mutex that marks a running instance. Stable across versions,
    /// deliberately: two different builds of Murmur fighting over one keyboard hook is just
    /// as bad as two copies of the same build.
    /// </summary>
    private const string SingleInstanceMutexName = "Murmur-SingleInstance-9c1f4e2a-70d3-4a5e-a9b2-6f8f3d1c5e77";

    /// <summary>Starts the app, or runs a headless self-test.</summary>
    /// <param name="args">Command line. <c>--selftest</c> exits without showing UI.</param>
    /// <returns>0 on success.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        // Single-instance guard. The app keeps running in the tray after its window closes
        // (ShutdownMode.OnExplicitShutdown), so a user relaunching the exe would otherwise
        // stack up a second keyboard hook, a second capture and a second tray icon. If the
        // mutex is already held by another process, exit before installing anything.
        using var instanceLock = new Mutex(
            initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);

        if (!isFirstInstance) return 0;

        try
        {
            // Last-resort diagnostic trail. The app is a WinExe living in the tray, so an
            // unhandled exception on any thread makes the whole process vanish with no
            // console and no dialog — this at least leaves the reason on disk.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                CrashLog.Write("Unhandled exception", e.ExceptionObject);

            PlatformFactory.InstallResolver();

            // The published single-file exe is the only artifact CI can run end to end, and a
            // GitHub runner cannot show a window. This branch exercises startup — assembly
            // loading, native library resolution out of the self-extracted bundle, model
            // discovery — and exits, which is the class of failure that only appears after
            // publishing.
            if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
            {
                return SelfTest.Run();
            }

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Released here rather than in a shutdown handler: Main's thread is the one that
            // acquired ownership, and ReleaseMutex must run on that thread.
            instanceLock.ReleaseMutex();
        }
    }

    /// <summary>Configures Avalonia. Also used by the headless test host.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>
/// Appends fatal or otherwise-invisible errors to <c>%LocalAppData%/Murmur/last-crash.log</c>.
/// </summary>
/// <remarks>
/// A separate file from <c>last-capture-error.log</c>, deliberately: that one records an
/// expected per-recording failure (a mic that refuses to open); this one means "the app
/// broke". Writing is best-effort — a crash logger that can itself throw is worse than none.
/// </remarks>
internal static class CrashLog
{
    /// <summary>Appends one entry. <paramref name="error"/> is usually an exception.</summary>
    public static void Write(string context, object? error)
    {
        try
        {
            var directory = Murmur.Core.MurmurPaths.DataDirectory;
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "last-crash.log"),
                $"[{DateTimeOffset.Now:O}] {context}: {error}{Environment.NewLine}");
        }
        catch
        {
            // Never let the logger become the crash.
        }
    }
}
