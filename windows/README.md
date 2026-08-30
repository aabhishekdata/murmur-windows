# Murmur for Windows

The Windows port of Murmur — push-to-talk dictation, on-device.

> **Status: working on real hardware.** A person has held the key, spoken, and watched the
> text land in another application. CI builds, tests and publishes a self-contained
> executable that passes its own self-test on Windows; 356 tests are green. What is still
> exercised only through fakes — real device enumeration, the Enter keystroke reaching a
> foreground app — is listed under [Honesty](#honesty).

---

## Why this is a rewrite, not a port

Almost every layer of the macOS app is Apple-specific:

| Layer | macOS | Windows |
|---|---|---|
| UI | SwiftUI | Avalonia |
| Audio capture | `AVAudioEngine` | WASAPI via NAudio |
| **Default speech engine** | `SpeechAnalyzer` (ships with macOS 26) | **nothing equivalent exists** |
| Parakeet | FluidAudio → CoreML | sherpa-onnx → ONNX Runtime |
| Hotkey | `CGEventTap` | `SetWindowsHookEx(WH_KEYBOARD_LL)` |
| Text injection | Accessibility API | `SendInput` |

The consequence that shapes everything: **Windows has no counterpart to Apple's
`SpeechAnalyzer`.** On macOS, Parakeet is the optional upgrade. On Windows it is the only
engine, and the app cannot transcribe until the model is downloaded —
see [`docs/PARAKEET-WINDOWS.md`](../docs/PARAKEET-WINDOWS.md).

What *is* genuinely shared is the dictionary's behaviour, and it is shared as a contract
rather than as code: [`shared/dictionary-test-vectors.json`](../shared/dictionary-test-vectors.json).
Both implementations run those vectors in CI. Changing correction semantics starts there.

---

## Decisions, and why

**Avalonia, not WPF or WinUI 3.** WPF cannot be run or UI-tested on macOS, so every mistake
would cost a full CI round-trip. Avalonia's headless test platform runs on macOS in ~100 ms,
including simulated keyboard input and real pixel capture. Win32 interop is unaffected —
hooks, `SendInput` and WASAPI are P/Invoke, not UI-framework code. WinUI 3 was rejected
outright: Microsoft's own docs contradict each other on whether unpackaged single-file
publishing works, with open bugs reporting an exe that won't launch.

**.NET 10, not .NET 8.** .NET 8 reaches end-of-life on **2026-11-10**.

**Right Ctrl is the default hotkey, not Right Alt.** Right Alt is AltGr on German, Polish,
UK, Nordic and most Latin-American layouts — it is how those users type `@`, `€`, `\`, `|`.
Binding push-to-talk there would break basic typing for a large fraction of users. Right
Ctrl produces no character on any layout.

**The hotkey is observed, never swallowed.** The macOS build consumes Right Option because
on macOS that key types characters. On Windows, suppression buys nothing and risks a much
worse failure: if the key-down is swallowed but the key-up escapes — a hook that timed out
mid-gesture, or focus crossing into an elevated window — the target app believes Ctrl is
held down forever.

**CPU-only inference.** sherpa-onnx ships no GPU package; DirectML is five versions behind
and forbids the variable tensor shapes this model requires; CUDA would force every user to
install a toolkit. On CPU with int8 weights, transcription runs ~40× faster than real time.

**Three pinned versions that would break at "latest":**

| Package | Pinned | Why |
|---|---|---|
| `NAudio` | **2.3.0** | 3.x targets .NET 9+ and will not restore |
| `Avalonia.Headless.XUnit` | **11.3.20** | 12.x requires xUnit **v3**, a different package line |
| `org.k2fsa.sherpa.onnx` | 1.13.5 | Bundles ONNX Runtime; never also reference `Microsoft.ML.OnnxRuntime` |

---

## The interface

Two windows and a tray icon.

**The floating bar** is the primary surface: a small dark pill, frameless, always on top,
never activated, kept out of the taskbar. It shows four states — at rest, listening (white
level bars that move with the voice), transcribing (pulsing dots), and "no model". Click it to
start and stop; drag it and it settles onto the nearest edge, turning vertical on a side; its
placement is stored as an edge plus a fraction (never pixels — a saved pixel position is
off-screen the moment a monitor is unplugged, and a frameless taskbar-less window that lands
off-screen cannot be recovered). Right-click for the menu.

**The hub** is the management window: a sidebar over Home (stats and searchable transcript
history, grouped by day), Dictionary and Settings.

`ShowActivated = false` on the bar is not cosmetic. Murmur types into whatever has focus, so a
bar that takes focus redirects the user's own dictation into itself.

Rendered previews of every screen and state live in `artifacts/ui-preview/`.

---

## The text pipeline

Six passes rewrite the transcript between the recogniser and the keyboard, and the order they
run in changes the answer. It is fixed in one place — `TextPipeline` — rather than emerging from
the order somebody added them to the engine:

```
filler removal → dictionary → self-correction → command extraction → snippets → list formatting
```

Each position is load-bearing, and `TextPipelineOrderTests` pins every one of them:

| Step | Why here |
|---|---|
| Filler removal | First, so "cloud um code" reaches the dictionary as "cloud code" |
| Dictionary | Before anything that reasons about words, so it reasons about the right ones |
| Self-correction | After the dictionary (the marker itself can be misheard), before snippets |
| Command extraction | **Before** expansion, so a snippet body ending "press enter" can never submit |
| Snippets | Trigger is now spelled right, has survived retraction, cannot reach the command |
| List formatting | Last — the only pass that inserts newlines; everything else matches flat text |

Every heuristic pass has a switch in Settings and every switch defaults on. The dictionary has
no switch: it is not a heuristic, it is what the user typed in.

**There is no language model anywhere in this app and there must not be one.** Every pass above
is deterministic pattern matching, which is what makes them exhaustively testable — and what
lets the app promise that nothing dictated ever leaves the machine.

---

## Layout

```
windows/
├─ Directory.Build.props          strict analysis, applied to every project
├─ Directory.Packages.props       central version pinning
├─ global.json                    SDK pin
├─ src/
│  ├─ Murmur.Dictionary/          corrections + biasing          net10.0
│  ├─ Murmur.Abstractions/        the four platform interfaces   net10.0
│  ├─ Murmur.Core/                engine, segmenter, storage,
│  │                              the text pipeline, snippets,
│  │                              bar placement                   net10.0
│  ├─ Murmur.Speech/              Parakeet via sherpa-onnx       net10.0
│  ├─ Murmur.Testing/             fakes for the interfaces       net10.0
│  ├─ Murmur.App/                 Avalonia UI                    net10.0
│  └─ Murmur.Platform.Windows/    the ONLY Win32 code            net10.0-windows
└─ tests/
   ├─ Murmur.Dictionary.Tests/    the shared vectors             24 tests
   ├─ Murmur.Core.Tests/          engine, chunking, storage,
   │                              every text pass and their
   │                              interactions, placement       267 tests
   └─ Murmur.App.Tests/           headless Avalonia UI,
                                  rendered previews              65 tests
```

**Only one project targets `-windows`.** Everything else is platform-neutral, so `CA1416`
turns an accidental Win32 call into a build error — and, more usefully, the whole app
builds, runs and tests on macOS.

`Murmur.App` loads the platform layer **by reflection** rather than referencing it. A direct
reference would drag the UI onto `net10.0-windows` and destroy the local loop. The published
self-test verifies that reflection works from inside the single-file bundle, because that is
where the arrangement would otherwise fail — silently, at the moment the user first pressed
the key.

Keeping the platform layer logic-free is deliberate: anything that lives there is code CI
cannot exercise. Retries, debouncing, device-change handling all belong in the neutral
projects, behind an interface.

---

## Building

**On Windows** — everything, including the platform layer:

```bash
cd windows
dotnet build Murmur.sln --no-incremental -warnaserror
dotnet test  Murmur.sln
```

**On macOS or Linux** — use the solution filter. `Murmur.Platform.Windows` targets
`net10.0-windows` and cannot compile off Windows; the filter omits it and everything else
builds and tests normally, including the full UI suite:

```bash
cd windows
dotnet test Murmur.CrossPlatform.slnf -c Release      # ~10s, 356 tests
```

`--no-incremental` is not optional in CI. Roslyn does not re-emit analyzer warnings on an
incremental build, so `-warnaserror` would pass on cached results and prove nothing.

---

## <a id="honesty"></a>Honesty about what is verified

**Verified, on Windows, every push:** 356 tests pass — 24 dictionary (the shared vectors),
267 core (dictation state machine, audio chunking, the storage formats, every text pass and the
order they run in, floating-bar placement), 65 headless Avalonia UI. CI then publishes a self-contained ~116 MB executable, **runs it**, and the
binary reports back that the dictionary works, the source-generated JSON round-trips, and
the Windows platform layer loads and constructs out of the bundle.

**Verified on macOS and Linux, in a few seconds:** the same 356 tests. The UI genuinely runs
headless here — with Skia rather than the no-op drawing backend, so every `Render` is really
executed — which is why bugs like a `Render` method mutating a property get caught while
writing them rather than three CI round-trips later. The same run rasterises every screen to
`artifacts/ui-preview/*.png`, which is the only way a redesign gets reviewed by someone who
cannot run the app on Windows.

**Known divergences between the two regex engines**, measured across 30 cases — 9 differed.
The two that affect this code are both handled: culture-sensitive case-insensitive matching
(fixed by `CultureInvariant`) and NFC/NFD mismatch (fixed by normalizing both sides). Two
that are *not* fixable are simply avoided: ICU folds `ß` to `ss` and .NET does not, and
.NET's `.` splits surrogate pairs. Neither is reachable from the patterns this code builds.

**Cannot be verified anywhere but a real machine:**

- Text injection into a foreground app. Runners have an interactive desktop but cannot take
  the foreground.
- A real microphone: device format negotiation, the OS microphone-privacy block, unplugging
  mid-capture.
- The low-level keyboard hook actually firing on a physical keypress.
- Parakeet transcribing real speech, and whether the ~2 GB working set is tolerable.

Everything those depend on is behind an interface and exercised with fakes, so the logic
around them is tested. The bindings themselves are not.
