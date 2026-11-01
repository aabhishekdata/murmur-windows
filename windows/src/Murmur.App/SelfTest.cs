using System.Runtime.InteropServices;
using Murmur.Core;
using Murmur.Dictionary;
using Murmur.Speech;

namespace Murmur.App;

/// <summary>
/// A headless check that the published binary actually works.
/// </summary>
/// <remarks>
/// Run in CI against the real single-file executable. It cannot show a window on a runner, so
/// it verifies the things that only break <i>after</i> publishing: assemblies resolving out
/// of the bundle, native libraries extracting, and paths resolving against
/// <c>AppContext.BaseDirectory</c> rather than the current directory.
/// </remarks>
public static class SelfTest
{
    /// <summary>Runs the checks.</summary>
    /// <returns>0 if everything passed.</returns>
    public static int Run()
    {
        var failures = 0;

        failures += CheckDictionary();
        failures += CheckStorage();
        failures += CheckModelDiscovery();
        failures += CheckPlatformLayer();

        Console.WriteLine(failures == 0 ? "self-test: PASS" : $"self-test: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static int CheckDictionary()
    {
        var failures = 0;

        var corrector = new DictionaryCorrector([DictionaryEntry.Correction("cloud code", "Claude Code")]);
        var (text, applied) = corrector.Apply("I use CloudCode daily.");

        failures += Check("dictionary rewrites glued words",
            text == "I use Claude Code daily." && applied.Count == 1);

        failures += Check("dictionary leaves similar words alone",
            corrector.Apply("Cloudflare is fine.").Text == "Cloudflare is fine.");

        return failures;
    }

    private static int CheckStorage()
    {
        // Exercises the source-generated JSON, which is the part most likely to have been
        // silently broken by trimming or single-file publishing.
        var directory = Path.Combine(Path.GetTempPath(), $"murmur-selftest-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(directory);

            var store = new TranscriptStore(Path.Combine(directory, "transcripts.jsonl"));
            store.Add(new TranscriptRecord
            {
                At = DateTimeOffset.UtcNow,
                Text = "Claude Code",
                Corrections = [new AppliedCorrection("cloud code", "Claude Code", 1)],
            });

            var reopened = new TranscriptStore(Path.Combine(directory, "transcripts.jsonl"));
            var ok = reopened.Records.Count == 1
                     && reopened.Records[0].Corrections?[0].To == "Claude Code";

            var settings = new AppSettings(Path.Combine(directory, "settings.json"));
            settings.Update(settings.Data with { PushToTalkKey = 0x7C });
            settings.Dispose();
            var settingsOk = new AppSettings(Path.Combine(directory, "settings.json")).Data.PushToTalkKey == 0x7C;

            // A saved prompt carries two custom converters (the match and the shortcut), which
            // is exactly the sort of thing that works in a build and not in a bundle.
            var snippets = new SnippetStore(Path.Combine(directory, "snippets.jsonl"));
            snippets.Add(new SnippetEntry
            {
                Trigger = "review prompt",
                Text = "Line one\nLine two",
                Match = SnippetMatch.WholeUtterance,
                Hotkey = HotkeyChords.Parse("Ctrl+Alt+1"),
            });
            var reopenedPrompts = new SnippetStore(Path.Combine(directory, "snippets.jsonl")).Entries;
            var prompt = reopenedPrompts.Count == 1 ? reopenedPrompts[0] : null;
            var promptOk = prompt is { Match: SnippetMatch.WholeUtterance, Hotkey: not null }
                           && HotkeyChords.Format(prompt.Hotkey.Value) == "Ctrl+Alt+1";

            return Check("history round-trips through source-generated JSON", ok)
                 + Check("settings round-trip", settingsOk)
                 + Check("a saved prompt round-trips with its shortcut", promptOk);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static int CheckModelDiscovery()
    {
        // A fresh machine has no model, and that must be a friendly message rather than a
        // crash on launch.
        var located = ParakeetTranscriber.Locate();
        Console.WriteLine($"  model: {located ?? "(not installed — expected on a clean runner)"}");

        return Check("model search paths are absolute",
            ParakeetTranscriber.DefaultSearchPaths().All(Path.IsPathRooted));
    }

    /// <summary>
    /// Verifies the platform layer resolves from inside the published bundle.
    /// </summary>
    /// <remarks>
    /// This is the riskiest thing in the app's structure. <c>Murmur.App</c> targets plain
    /// <c>net10.0</c> and loads <c>Murmur.Platform.Windows</c> by reflection, which is what
    /// lets the UI be built and headless-tested on macOS. Reflection into a single-file
    /// bundle is exactly where that arrangement would fail — and it would fail at the moment
    /// the user first pressed the hotkey, not at startup. So it is checked here, on Windows,
    /// against the real published executable.
    /// </remarks>
    private static int CheckPlatformLayer()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("  [skip] platform layer (not Windows)");
            return 0;
        }

        var failures = Check("Windows platform assembly loads from the bundle", PlatformFactory.IsAvailable);
        if (!PlatformFactory.IsAvailable) return failures;

        failures += Check("audio capture constructs",
            PlatformFactory.CreateAudioCapture(static () => null) is not null);
        failures += Check("keystroke sender constructs", PlatformFactory.CreateKeystrokeSender() is not null);

        // Enumeration is what the microphone picker in Settings and on the bar is built on, and
        // it is reflection-resolved like everything else in the platform layer — so it is worth
        // proving it loads from the published bundle rather than only from a build output.
        var microphones = PlatformFactory.CreateAudioDeviceCatalog();
        failures += Check("microphone catalog constructs", microphones is not null);
        Console.WriteLine($"  microphones: {microphones?.List().Count ?? 0}");

        // Constructed, not started: installing a real low-level keyboard hook on a CI runner
        // is neither useful nor polite.
        var hotkey = PlatformFactory.CreateHotkeySource(0xA3);
        failures += Check("hotkey source constructs with Right Ctrl", hotkey is not null);

        // Esc-to-cancel is an optional interface; if the hook lost it, Esc would silently stop
        // cancelling, and nothing else would notice.
        failures += Check("hotkey source can watch Esc", hotkey is Murmur.Abstractions.ICancelKeySource);
        hotkey?.Dispose();

        failures += CheckSecretStorage();
        failures += CheckGlobalHotkeys();

        return failures;
    }

    /// <summary>
    /// Registers and gives back a combination nobody uses, on the bindings' own thread.
    /// </summary>
    /// <remarks>
    /// A prompt shortcut that silently fails to register looks exactly like one the user never
    /// pressed. Ctrl+Alt+Shift+F24 is on almost no keyboard, so claiming it for a moment
    /// disturbs nothing; registering it twice proves the claim is real rather than a no-op.
    /// </remarks>
    private static int CheckGlobalHotkeys()
    {
        using var hotkeys = PlatformFactory.CreateGlobalHotkeys();
        var failures = Check("global hotkeys construct", hotkeys is not null);
        if (hotkeys is null) return failures;

        var chord = HotkeyChords.Parse("Ctrl+Alt+Shift+F24");
        var registered = hotkeys.Register(1, chord);
        failures += Check("a prompt shortcut registers", registered);

        using (var second = PlatformFactory.CreateGlobalHotkeys())
        {
            failures += Check("a registered shortcut is refused to a second owner",
                registered && second is not null && !second.Register(1, chord));
        }

        hotkeys.Unregister(1);

        using (var third = PlatformFactory.CreateGlobalHotkeys())
        {
            var reclaimed = third is not null && third.Register(1, chord);
            failures += Check("an unregistered shortcut is free again", reclaimed);
            if (reclaimed) third!.Unregister(1);
        }

        return failures;
    }

    /// <summary>
    /// Round-trips a made-up key through DPAPI and the secret file, in a scratch folder.
    /// </summary>
    /// <remarks>
    /// The Azure key is only ever as safe as this binding, and it is the one platform call whose
    /// failure would not show until someone saved a key and restarted. It is cheap to prove here:
    /// DPAPI needs no hardware and no prompt, and works on a CI runner's account.
    /// </remarks>
    private static int CheckSecretStorage()
    {
        var protector = PlatformFactory.CreateDataProtector();
        var failures = Check("DPAPI protector constructs", protector is not null);
        if (protector is null) return failures;

        var directory = Path.Combine(Path.GetTempPath(), $"murmur-selftest-{Guid.NewGuid():N}");
        try
        {
            const string secret = "selftest-not-a-real-key-0123456789";
            var store = new ProtectedSecretFile(directory, protector);
            store.Write(CloudKey.SecretName, secret);

            var onDisk = File.ReadAllBytes(store.PathFor(CloudKey.SecretName));
            var plaintext = System.Text.Encoding.UTF8.GetBytes(secret);
            var leaked = onDisk.AsSpan().IndexOf(plaintext) >= 0;

            failures += Check("DPAPI round-trips a key through the secret file",
                new ProtectedSecretFile(directory, protector).Read(CloudKey.SecretName) == secret);
            failures += Check("the secret file does not hold the key in the clear", !leaked);
            return failures;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static int Check(string name, bool passed)
    {
        Console.WriteLine($"  [{(passed ? "ok" : "FAIL")}] {name}");
        return passed ? 0 : 1;
    }
}
