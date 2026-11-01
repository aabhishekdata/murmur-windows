# Murmur

Push-to-talk dictation that runs on your own machine by default. Hold a key, talk, release —
cleaned-up text lands in whatever text field has focus. No account, and out of the box no
cloud: audio stays on the device.

**Privacy, in one paragraph.** On Windows, Parakeet on your PC is the default engine and the
offline fallback. Cloud transcription is opt-in and uses *your own* Azure OpenAI resource and
key (`gpt-4o-transcribe` or `gpt-4o-mini-transcribe`); audio leaves the machine only while one
of the two Azure engine modes is selected, and only to the endpoint you entered. The key is
encrypted with DPAPI for your Windows account in its own file — it is never written to
`settings.json`. Speaker labelling (diarization) is not supported. The macOS app has no cloud
path at all.

This repository holds two implementations:

| | Status |
|---|---|
| **[`windows/`](windows/) — the Windows app** | Actively developed. Runs on real hardware; 935 tests green. **Start here.** |
| `Sources/`, `Tests/` — the macOS app | Upstream code, unchanged. See [The macOS app](#the-macos-app) below. |

---

## Credit

The original project — the macOS app, the architecture, and the dictionary contract that
both platforms implement — is **[per-simmons/murmur-youtube](https://github.com/per-simmons/murmur-youtube)**.
This repository is a derivative work: the Windows port in `windows/` was built on top of it.
All macOS code and the shared design remain the original author's.

---

## Quick start (Windows)

You need nothing installed to *run* it — the published executable is self-contained.

1. Grab `Murmur-win-x64.zip` from the [Releases](../../releases) page and unzip it to a
   folder you can write to (not `C:\Program Files`).
2. Run `get-model.ps1` once to download the Parakeet speech model (~661 MB). Windows has no
   built-in speech engine this app can use, so it cannot transcribe until the model is on disk.
3. Run `Murmur.App.exe`. SmartScreen will warn that it's unsigned — *More info* → *Run anyway*.
4. Hold **Right Ctrl**, speak, release.

Also worth knowing: **double-tap** Right Ctrl to keep listening hands-free (tap once more to
stop), **Esc** cancels a dictation and types nothing, and a shortcut you choose in Settings
(none by default) pastes your last transcript again. History can paste any transcript and retry the last few with the other engine;
Home shows words, minutes, time saved and an estimate of what the cloud minutes cost. The bar's
badge says where your voice went — a laptop for this PC, a cloud for your Azure resource.

Optional, and off unless you choose it: to transcribe with your own Azure OpenAI resource
instead, open **Settings → Transcription engine**, pick *Azure* or *Azure, offline fallback*,
enter the endpoint, the `gpt-4o-transcribe` (Accurate) and/or `gpt-4o-mini-transcribe` (Fast)
deployment names and your key, then press **Test connection**. The tray and bar menus switch
between Accurate and Fast without a restart.

`windows/artifacts/publish/README-FIRST.md` ships alongside the binary with the same steps.

## Building it yourself (Windows)

```bash
cd windows
dotnet build Murmur.sln -c Release          # needs the .NET SDK in global.json
dotnet test  Murmur.sln -c Release          # 935 tests
dotnet publish src/Murmur.App/Murmur.App.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o artifacts/publish
```

Building **on** Windows is the supported path — the MSBuild targets that copy
`Murmur.Platform.Windows.dll` and the NAudio libraries beside the executable are gated on
`IsWindows()`. Cross-compiling from macOS or Linux works for everything else if you add
`-p:EnableWindowsTargeting=true`, but you must then publish `Murmur.Platform.Windows`
separately and copy its output beside the exe yourself; without those files the app starts
and immediately fails to open a microphone.

The full architecture, the platform decisions, and an honest account of what is and isn't
verified are in **[`windows/README.md`](windows/README.md)**.

## Building the macOS app

```bash
make install     # builds, bundles, signs, copies to /Applications, launches
```

Details, permissions and the TCC notes are below.

---

# <a id="the-macos-app"></a>The macOS app

*Everything from here down is the original project's documentation, preserved as-is.*

---

## Coexisting with another dictation app

This app is built to run alongside other dictation tools without colliding with them, which
is not automatic on macOS and is worth understanding before changing anything:

- **Bundle ID `ai.pivotstudio.murmur-youtube`** — TCC keys Accessibility and Microphone
  grants to the bundle ID, so granting or revoking a permission here has no effect on any
  other app, and vice versa.
- **Executable `MurmurYouTube`** — distinct enough that `pkill -x MurmurYouTube` cannot
  match a differently-named binary. The `Makefile` only ever targets `$(EXEC)`.
- **Hotkey is configurable** (Right ⌥ / fn / Right ⌘) precisely because another tool may
  already own the key you'd reach for first. The event tap inspects only its own keycode
  and passes everything else through untouched.

If you run more than one dictation app, give each a different push-to-talk key. Two apps on
the same key both record, and whichever injects text will fight the other.

---

## Quick start

```bash
make install     # builds, bundles, signs, copies to /Applications, launches
```

Then grant two permissions — neither is optional, and neither can be requested silently:

| Permission | Where | Needed for |
|---|---|---|
| **Accessibility** | System Settings ▸ Privacy & Security ▸ Accessibility | The `CGEventTap` that sees the hotkey, and the AX text insert |
| **Microphone** | Prompted on first dictation | Audio capture |

Restart Murmur YouTube after granting Accessibility. Then hold **Right ⌥** and talk.

### Why grants survive rebuilds here

TCC stores a *code-signing requirement* per entry, not just a path. An ad-hoc signature
changes on every build, so the rebuilt binary stops satisfying the stored requirement —
and the symptom is nasty: the Accessibility toggle still **shows as on** while the app is
reported untrusted, and flipping it changes nothing because the stale row is the problem.

The `Makefile` therefore signs with a stable Developer ID (auto-detected via
`security find-identity`, falling back to ad-hoc). Verified: rebuild + reinstall keeps both
grants with no re-prompt.

If a grant ever does get wedged, reset that one row and re-add — never toggle:

```bash
tccutil reset Accessibility ai.pivotstudio.murmur-youtube
tccutil reset Microphone   ai.pivotstudio.murmur-youtube
```

Always pass the bundle ID. A bare `tccutil reset Accessibility` wipes **every** app on the
machine. Then quit System Settings entirely (⌘Q) before reopening — that pane caches its
list and will otherwise show the row you just deleted.

> **Keep the build out of iCloud.** `~/Desktop` and `~/Documents` are file-provider synced
> on this machine; the sync engine can materialize/dematerialize files inside an `.app` and
> corrupt its signature. `make install` puts the running copy in `/Applications`.

Other targets: `make app` (bundle only), `make run` (run in place), `make clean`.

---

## Architecture

```
 hold key ─► HotkeyMonitor ──► DictationController ◄── Settings
                                │
                     ┌──────────┼──────────┐
                     ▼          ▼          ▼
              AudioCapture  HUDPanel   TranscriptionEngine
                     │                      │
                (AudioChunk) ──ordered──► AppleSpeechEngine
                                            │
                                       (transcript)
                                            ▼
                                      TextFormatter
                                            ▼
                                      TextInjector ─► focused app
```

### Decisions worth knowing

**The HUD must never take focus.** `HUDPanel` is a `.nonactivatingPanel` with
`canBecomeKey == false`. This is the load-bearing detail of the whole app: if the overlay
took key status, the user's text field would lose focus and there'd be nothing left to
inject into. Everything else is replaceable; this isn't.

**The hotkey needs a `CGEventTap`, not `NSEvent`.** `fn` and left/right modifier
discrimination don't surface through `NSEvent.addGlobalMonitorForEvents` or the Carbon
hotkey API. A session event tap is the only way to see them — which is why Accessibility
permission is a hard requirement rather than a nicety.

**Audio ordering is explicit.** `AudioCapture` yields into an `AsyncStream` drained by a
single task. Spawning a `Task` per buffer would be simpler and would silently corrupt the
transcript, because unstructured tasks have no ordering guarantee.

**Buffers are copied, never borrowed.** `AVAudioEngine` recycles the buffer it hands to a
tap the instant the callback returns. `AudioChunk`'s `@unchecked Sendable` is only sound
because `AudioCapture` always allocates fresh storage before handing off.

**Two swappable seams.** `TranscriptionEngine` and `TextFormatter` are protocols so the
two components most likely to change can change without touching anything else.

### Layout

```
Sources/MurmurYouTube/
├── MurmurYouTubeApp.swift              @main, AppDelegate, MenuBarExtra
├── Core/
│   ├── DictationController.swift   state machine, wires everything
│   ├── HotkeyMonitor.swift         CGEventTap on .flagsChanged
│   ├── AudioCapture.swift          AVAudioEngine tap + format conversion + RMS
│   └── TextInjector.swift          AX insert, pasteboard+⌘V fallback
├── Transcription/
│   ├── TranscriptionEngine.swift   protocol + AudioChunk
│   └── AppleSpeechEngine.swift     SpeechAnalyzer / SpeechTranscriber
├── Formatting/
│   └── TextFormatter.swift         protocol + RuleBasedFormatter
├── UI/
│   ├── HUDPanel.swift              non-activating floating panel
│   └── HUDView.swift               waveform + live transcript, Brand palette
└── Support/
    ├── Settings.swift, Permissions.swift, Log.swift
```

---

## Speech engine

Default is Apple's **`SpeechAnalyzer` / `SpeechTranscriber`**, new in macOS 26: no
dependency, no bundled model, no cloud path, real streaming with `.volatileResults` so
text appears while you're still talking. The OS downloads and manages model assets, so the
first run for a locale may pause on `AssetInstallationRequest`.

The intended upgrade is **Parakeet v3** via FluidAudio (CoreML on the Neural Engine) —
measurably better English WER, ~110× realtime, ~66 MB resident. Implementing
`TranscriptionEngine` is the entire cost of switching; `DictationController` doesn't
change.

| | Apple SpeechTranscriber | Parakeet v3 (FluidAudio) | Whisper large-v3 (WhisperKit) |
|---|---|---|---|
| Dependency | none | SwiftPM | SwiftPM |
| Model download | OS-managed | ~600 MB | ~1.5 GB |
| English accuracy | good | best | good |
| Languages | many | 25 | 99 |
| Latency | low | ~80 ms | 200–500 ms |

---

## Not built yet

1. **LLM cleanup tier.** `RuleBasedFormatter` strips fillers, fixes spacing, capitalizes
   sentences and adds terminal punctuation — genuinely useful, entirely deterministic. The
   real win is a second `TextFormatter` backed by Apple's on-device Foundation Models
   (macOS 26) for tone, list formatting, and honoring spoken corrections, with Claude as an
   optional higher-quality tier.
2. **Command Mode.** Select text, hold a second hotkey, say "make this more formal."
   Needs AX read of `kAXSelectedTextAttribute` plus an LLM round-trip.
3. **Personal dictionary.** Names and jargon the ASR keeps missing. `SpeechAnalyzer`
   supports this through `AnalysisContext` / `SFCustomLanguageModelData`.
4. **Branding.** `Brand` in `HUDView.swift` is a two-color placeholder gradient. App icon,
   real palette, HUD motion design, onboarding.
5. **Onboarding.** A first-run window that walks through both permissions instead of
   relying on the menu's "Grant…" items.
6. **Developer ID signing + notarization.** Ends the TCC-reset churn and makes the app
   distributable.

---

## Verified

Driven with a synthetic Right ⌥ hold (`scratchpad/ptt/ptt2.swift` posts `flagsChanged`
events) and confirmed via `/usr/bin/log show --predicate 'subsystem ==
"ai.pivotstudio.murmur-youtube"'`:

- Builds clean under Swift 6 strict concurrency.
- Signs with Developer ID; grants survive rebuild + reinstall.
- Launches as an accessory app, no Dock icon, menu bar item present.
- Event tap arms on grant without a restart (the poller catches it).
- Full state machine: `starting → listening → finishing → idle`, no errors.
- `SpeechAnalyzer` starts; models already installed, no download stall.
- Audio capture runs and converts native 48 kHz → 16 kHz for the engine.
- HUD renders bottom-center at `{{790, 96}, {340, 76}}` without taking focus.
- Silence produces an empty transcript and injects nothing.

**Not yet verified:** speech → transcript → cleanup → injection. Synthetic key events
can't produce audio, so this needs a human to hold the key and talk.

> `log` is shadowed in this shell — use `/usr/bin/log` explicitly or it returns nothing.
