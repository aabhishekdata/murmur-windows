# Murmur for Windows

The Windows port of Murmur — push-to-talk dictation, on-device by default, with optional
transcription through your own Azure OpenAI key.

> **Status: working on real hardware.** A person has held the key, spoken, and watched the
> text land in another application. CI builds, tests and publishes a self-contained
> executable that passes its own self-test on Windows; 935 tests are green. What is still
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
offline engine, and the app cannot transcribe offline until the model is downloaded (or until
you opt in to Azure with your own key) —
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

**Pinned versions that would break at "latest":**

| Package | Pinned | Why |
|---|---|---|
| `NAudio` | **2.3.0** | 3.x targets .NET 9+ and will not restore |
| `Avalonia.*` | **12.1.4**, all together | 12 lets the render loop sleep when nothing animates (11 ticked forever); an 11 assembly mixed into 12 builds and then fails at run time |
| `xunit.v3` | **3.2.2** | `Avalonia.Headless.XUnit` 12 is built on the v3 3.x extensibility API; 4.x is a new major. Test projects are `OutputType Exe` |
| `FluentAvaloniaUI` | 3.1.0 | First line built for Avalonia 12.1; its theme replaces `FluentTheme` (see `Design/AppTheme.cs`) |
| `org.k2fsa.sherpa.onnx` | 1.13.5 | Bundles ONNX Runtime; never also reference `Microsoft.ML.OnnxRuntime` |

`Avalonia.Diagnostics` has no 12.x release and is no longer referenced.

---

## The interface

Two windows and a tray icon, in the Windows 11 Fluent style (see AGENTS.md → Design system).

**The floating bar** is the primary surface: a small dark capsule at the bottom centre,
frameless, always on top, never activated, kept out of the taskbar. It shows its states — at
rest (a dim dot), recording (red dot and white level bars that move with the voice),
hands-free ("Hands-free — tap to stop"), transcribing (the word, no spinner), and what is in the
way ("No model", "Add API key"). A badge beside the dot says where your voice goes: a laptop for
Parakeet on this PC, a cloud for your Azure resource, a struck-out cloud when the offline fallback
answered — in the bar's muted ink, never red. After a fallback or a dictation that could not be
transcribed, one short sentence ("Azure unreachable — used Parakeet", "Key rejected — nothing
typed") shows at rest for a few seconds.
Click it to start and stop; drag it and it settles onto the nearest edge, turning vertical on a
side; its placement is stored as an edge plus a fraction (never pixels — a saved pixel position
is off-screen the moment a monitor is unplugged, and a frameless taskbar-less window that lands
off-screen cannot be recovered). Right-click for the menu.

**The keyboard:** hold the push-to-talk key to dictate; **double-tap** it to latch hands-free and
tap it again to stop; a single quick tap dictates like a short hold. **Esc** cancels whatever is
being recorded or transcribed and types nothing, up until the text is ready (the only key Murmur
ever swallows, and only then); a paste-last shortcut you record in Settings (none by default, so
nothing is taken from other apps) pastes the last transcript again. The tray and the bar menu have *Copy last transcript*.

**The hub** is the management window: Mica, a 32 px custom title bar, and a navigation pane
over Home (status, a usage dashboard — words, minutes, time saved, the split by engine and the
estimated cloud cost, at a typing speed and per-minute prices you can change — suggestions,
recent dictation), History (searchable, grouped by day, each row saying which engine heard it,
with Paste and, for the last five, *Retry with the other engine*), Dictionary, Prompts & snippets
and Settings — groups of Windows 11 setting cards. Light, dark and
high contrast switch live, and the accent follows Windows' unless it is red.

**First-run setup** walks a new install through the engine, Azure or the offline model, and the
push-to-talk key with a live "hold it and say hello" check. Anyone upgrading skips it.

`ShowActivated = false` on the bar is not cosmetic. Murmur types into whatever has focus, so a
bar that takes focus redirects the user's own dictation into itself.

Rendered previews of every screen and state live in `artifacts/ui-preview/`.

---

## The text pipeline

Six passes rewrite the transcript between the recogniser and the keyboard, and the order they
run in changes the answer. It is fixed in one place — `TextPipeline` — rather than emerging from
the order somebody added them to the engine:

```
filler removal → dictionary → self-correction → command extraction → [saved prompt?] → snippets → list formatting
```

Each position is load-bearing, and `TextPipelineOrderTests` pins every one of them:

| Step | Why here |
|---|---|
| Filler removal | First, so "cloud um code" reaches the dictionary as "cloud code" |
| Dictionary | Before anything that reasons about words, so it reasons about the right ones |
| Self-correction | After the dictionary (the marker itself can be misheard), before snippets |
| Command extraction | **Before** expansion, so a snippet body ending "press enter" can never submit |
| Saved prompt (4b) | If the whole utterance is a prompt's trigger, its text is pasted verbatim and the run stops |
| Snippets | Trigger is now spelled right, has survived retraction, cannot reach the command |
| List formatting | Last — the only pass that inserts newlines; everything else matches flat text |

A **saved prompt** is a snippet marked "Say only this": it fires only when its trigger is all
that was said (case, punctuation, hyphens and spacing ignored), and its text arrives exactly as
written — pasted through the clipboard, which is then put back. Both writes are marked
(`CanIncludeInClipboardHistory = 0`, `CanUploadToCloudClipboard = 0`), so prompts and dictation stay out of Win+V history and
cloud clipboard sync. "Review prompt, press enter"
pastes it and sends it. A prompt can also have a key combination (Ctrl+Alt+1, say), registered
with `RegisterHotKey`; pressing it mid-recording discards the recording and pastes the prompt.
`snippets.jsonl` files written before prompts existed load unchanged, as inline snippets.

Every heuristic pass has a switch in Settings and every switch defaults on. The dictionary has
no switch: it is not a heuristic, it is what the user typed in.

**There is no language model in the text pipeline and there must not be one.** Every pass above
is deterministic pattern matching, which is what makes them exhaustively testable — and it runs
on the PC whichever engine heard the words.

---

## Speech engines and the privacy contract

| Mode | What hears you | Does audio leave the PC? |
|---|---|---|
| **Offline (Parakeet)** — the default | Parakeet, on this PC | No |
| **Azure** | Your own Azure OpenAI deployment | Yes, to the endpoint you entered |
| **Azure, offline fallback** | Azure; Parakeet when Azure cannot be reached, times out, or answers 408/429/5xx | Yes, to the endpoint you entered |

- **Parakeet is the default and the fallback.** Cloud transcription is opt-in, uses *your* Azure
  resource and *your* key, and nothing is ever sent while the offline mode is selected.
- **Two cloud models, each its own deployment:** *Accurate* = `gpt-4o-transcribe` (the default
  profile) and *Fast* = `gpt-4o-mini-transcribe`. If only one deployment is filled in, that one
  is used. The tray and bar menus switch between them; it applies on the next dictation.
- **Diarization is not supported** — `gpt-4o-transcribe-diarize` rejects the prompt that
  carries your dictionary terms.
- **Language:** *Detect automatically*, *English*, *Hindi* (sent as `hi`) or *Hinglish* (no
  language code, plus a prompt asking for Hindi in the Latin alphabet mixed with English). These
  are for the Azure engines; Parakeet is English-only, Settings warns about it, and a fallback in
  those modes is labelled "English-only fallback".
- **Retry with the other engine** keeps the contract: it is offered towards Azure only while an
  Azure mode is chosen and set up. The audio it needs — the last five dictations — is held in
  memory, and written to disk (for 24 hours, then deleted) only if you turn on *Keep recent audio
  for 24 hours* in Settings ▸ Privacy & data.
- **The key** is entered once in Settings, encrypted with DPAPI for your Windows account
  (CurrentUser scope) and kept in `%LocalAppData%\Murmur\secrets\azure-key.bin`. It is never
  written to `settings.json`, logs or error messages, and the UI only ever shows its last four
  characters. If the file cannot be decrypted (a profile restored onto another PC), Settings
  asks for the key again.
- A rejected key (401), a resource whose network rules shut this PC out (403), a missing
  deployment (404) or a malformed request (400) is shown, not papered over with Parakeet: falling
  back on those would hide a broken setup. Only a 401 marks the key rejected — a 403 is reported
  as "Azure blocked this network". Only network trouble, timeouts and 408/429/5xx fall back, and
  only when the offline model is installed; without it the cloud is always asked. The bar shows *Add API key*, *Set up Azure* or
  *Key rejected* when the chosen engine cannot work.
- Requests go through one shared `HttpClient` using the system proxy with your Windows
  credentials, so corporate proxies work; a proxy that still refuses is reported as 407.
- **Test connection** sends one second of quiet tone to the profile in use (or, with *Test
  both*, to each filled-in deployment) and reports status, latency and the Azure region.

---

## Layout

```
windows/
├─ Directory.Build.props          strict analysis, applied to every project
├─ Directory.Packages.props       central version pinning
├─ global.json                    SDK pin
├─ src/
│  ├─ Murmur.Dictionary/          corrections + biasing          net10.0
│  ├─ Murmur.Abstractions/        the platform interfaces        net10.0
│  ├─ Murmur.Core/                engine, segmenter, storage,
│  │                              the text pipeline, snippets,
│  │                              engine routing, the secret
│  │                              file, bar placement             net10.0
│  ├─ Murmur.Speech/              Parakeet via sherpa-onnx       net10.0
│  ├─ Murmur.Cloud/               Azure OpenAI transcription:
│  │                              HTTP, retries, WAV, prompt     net10.0
│  ├─ Murmur.Testing/             fakes for the interfaces       net10.0
│  ├─ Murmur.App/                 Avalonia UI                    net10.0
│  └─ Murmur.Platform.Windows/    the ONLY Win32 code            net10.0-windows
└─ tests/
   ├─ Murmur.Dictionary.Tests/    the shared vectors             24 tests
   ├─ Murmur.Core.Tests/          engine, chunking, storage,
   │                              every text pass and their
   │                              interactions, routing,
   │                              the key's storage, placement,
   │                              hands-free, Esc, paste-last,
   │                              retry, usage stats            568 tests
   ├─ Murmur.Cloud.Tests/         requests, retries, errors,
   │                              WAV bytes, prompt, endpoint    64 tests
   └─ Murmur.App.Tests/           headless Avalonia UI,
                                  engine wiring, previews       279 tests
```

`Murmur.Cloud` references `Murmur.Abstractions` only and is an ordinary reference of
`Murmur.App`, so it is compiled into the single-file exe. Its tests answer every request from a
stub `HttpMessageHandler`: **no test makes a network call.**

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
dotnet test Murmur.CrossPlatform.slnf -c Release      # ~15s, 935 tests
```

`--no-incremental` is not optional in CI. Roslyn does not re-emit analyzer warnings on an
incremental build, so `-warnaserror` would pass on cached results and prove nothing.

---

## <a id="honesty"></a>Honesty about what is verified

**Verified, on Windows, every push:** 935 tests pass — 24 dictionary (the shared vectors),
568 core (dictation state machine, audio chunking, the storage formats and their backward
compatibility, every text pass and the order they run in, engine routing and fallback, the key's
encrypted storage, floating-bar placement, the hands-free gesture's timing table, Esc arming and
cancelling, paste-last, retry, the usage arithmetic), 64 cloud (request shape, multipart fields,
WAV bytes, retry and no-retry tables, `Retry-After`, timeout versus cancellation, the size guard,
the key never leaking), 279 headless Avalonia UI. CI then publishes a self-contained ~116 MB executable, **runs it**, and the
binary reports back that the dictionary works, the source-generated JSON round-trips, the
Windows platform layer loads and constructs out of the bundle, and DPAPI round-trips a key
through the secret file without it appearing in the clear.

**Verified on macOS and Linux, in a few seconds:** the same 935 tests. The Azure
client and its tests are platform-neutral; only the DPAPI binding is Windows-only, and the
self-test covers it. The UI genuinely runs
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
- A real Azure OpenAI resource answering: the tests never make a network call, so the request
  shape is checked against the documented API, not against Azure itself. **Test connection** in
  Settings is the check, and the first thing to run on a new setup.

Everything those depend on is behind an interface and exercised with fakes, so the logic
around them is tested. The bindings themselves are not.
