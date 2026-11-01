# Working on this repo

Read this before changing anything. It is written for a coding agent picking the project up
cold, and it is mostly a list of things that look wrong but aren't, plus things that look
fine and will bite you.

---

## What this is

Push-to-talk dictation. Hold a key, talk, release, and cleaned-up text is typed into
whatever had focus. Two independent implementations:

| | macOS | Windows |
|---|---|---|
| Language | Swift 6 | C# / .NET 10 |
| UI | SwiftUI | Avalonia |
| Speech | Apple `SpeechAnalyzer`, or Parakeet via FluidAudio | Parakeet via sherpa-onnx (default); opt-in Azure OpenAI with the user's own key |
| Location | repo root | `windows/` |

**The macOS app works and is in daily use.**

**The Windows app works on real hardware.** A person has held the key, spoken, and watched
the text land in another application. CI builds it, runs 935 tests, publishes a single-file
executable, launches it on Windows and confirms the platform layer loads and constructs.
Individual bindings that CI cannot reach — device enumeration against real hardware, the
Enter keystroke reaching a foreground app — are still fake-only; say so specifically rather
than hedging the whole app.

---

## The privacy contract (Windows)

This is a promise to users, so treat it like the vectors: change it only on purpose, and change
the docs, the Settings wording and the tests with it.

- **Parakeet, offline, is the default engine and the fallback.** A fresh install sends nothing
  anywhere.
- **Cloud transcription is opt-in and bring-your-own-key:** the user's own Azure OpenAI
  resource, `gpt-4o-transcribe` (Accurate, the default profile) or `gpt-4o-mini-transcribe`
  (Fast). Audio leaves the machine **only** while an Azure engine mode is selected, and only to
  the endpoint the user typed. No other service, no telemetry.
- **The key** is DPAPI-encrypted for the current Windows user in
  `%LocalAppData%\Murmur\secrets\azure-key.bin`. It is **never** in `settings.json`
  (`CloudKeyTests` serialises every setting to prove it), never in a log, an exception message or
  a `ToString()`, and the UI shows only its last four characters.
- **Fallback never hides a broken setup.** Only network failures, timeouts and 408/429/5xx fall
  back to Parakeet; 400/401/403/404 are shown to the user. A dictation the user cancelled is
  never retried anywhere.
- **The text pipeline has no language model** and runs locally whichever engine heard the
  words.
- **Diarization is not supported** (`gpt-4o-transcribe-diarize` rejects the prompt).
- **History's "Retry with the other engine" obeys the same rule.** Retrying with Azure is offered
  only while an Azure mode is chosen and set up (`TranscriptRetry.MayUse`); a Parakeet-mode user
  is never offered it. Retrying with Parakeet is always local.
- **Recent audio is in memory unless the user opts in.** `RecentAudio` keeps the last five
  utterances (at most ten minutes between them) for that retry, only while history is kept, and
  forgets them on delete, on "Delete all" and on exit. Only with "Keep recent audio for 24 hours"
  (`KeepRecentAudioOnDisk`, off by default) is each one also written to
  `%LocalAppData%\Murmur
ecent-audio`; a file is never read back after 24 h, is purged at start-up
  and by a timer armed only while files exist, and every file is deleted when the setting goes off.
- **Language:** Hindi sends `language=hi`; Hinglish sends no language and a prompt sentence
  asking for romanised Hindi (`LanguageModes`). Parakeet is English-only — Settings says so, and a
  fallback in those modes is reported as "English-only fallback".
- **Tests never make a network call.** `Murmur.Cloud.Tests` answers every request from
  `StubHttpMessageHandler`, which fails rather than falls through when nothing is scripted.

---

## The one rule that matters

**`shared/dictionary-test-vectors.json` is the specification for correction behaviour.**

Both implementations run it in CI. If you change how corrections work, change the vectors
first, watch both sides go red, then make them green. Changing one implementation to "fix"
a failing vector without changing the other is how the two silently diverge — and only one
of them can be exercised by hand.

```bash
swift test --filter VectorTests                    # macOS side
cd windows && dotnet test Murmur.CrossPlatform.slnf # Windows side, runs anywhere
```

The Swift copy at `Tests/MurmurDictionaryTests/dictionary-test-vectors.json` is a copy, and
CI fails if it drifts from `shared/`. After editing the shared file:

```bash
cp shared/dictionary-test-vectors.json Tests/MurmurDictionaryTests/
```

---

## Things that look like bugs and are not

**`dotnet build Murmur.sln` fails on macOS** with `NETSDK1073`. Expected —
`Murmur.Platform.Windows` targets `net10.0-windows`. Use `Murmur.CrossPlatform.slnf`, which
omits it; everything else, including the whole UI suite, builds and tests on macOS in about
half a second.

**`swift build` fails with "input file was modified during the build."** The repo lives in an
iCloud-synced folder and the sync engine touches files mid-compile. **Always build with
`make`**, which uses `--scratch-path` outside the synced tree. A bare `swift build` also
writes a `.build/` directory into iCloud, which makes every subsequent build minutes slower.
If you see this error, wait a few seconds and retry.

**Compare mode doesn't type anything.** By design — `Settings.compareMode` runs every engine
on one recording and shows them side by side. If both injected, two transcripts would fight
over one text field. This is the single most confusing behaviour in the app.

**The timing column isn't comparing like with like.** Apple and Parakeet are timed on local
compute with the clock started *after* model load. Wispr Flow's number is its own
`e2eLatency`, which includes a network round trip and its cleanup pass. Don't present them
as one ranking.

**`MainActor.assumeIsolated` will crash the process.** It does not check the claim, it
asserts it. Use `await MainActor.run` from any non-main-actor context. This took the app
down once already.

**Mutating `@State` inside a `Canvas` draw closure floods the log and corrupts state.** The
VU meter keeps its needle physics in a plain reference type the view merely holds, which is
invisible to SwiftUI's state graph. Don't "clean that up" into `@State`.

---

## Design system

The two apps share rules, not pixels. **Red means recording, on both platforms, and nothing
else is red** — not a destructive button, not an error, not the accent. That one is not
negotiable anywhere. **There are no gradients anywhere.** Views contain no literal values on
either side: if a component needs a number that isn't a token, add the token.

### macOS: field recorders

`Sources/MurmurYouTube/UI/DesignSystem.swift` defines every colour, size, radius, duration
and material token.

The direction is 1980s field recorders — Sony TC-D5, Marantz PMD, Nakamichi, Braun. Silver
face in light appearance, black face in dark. Beyond red-means-recording:

- **Amber and green are instrumentation only** — level meters, never UI chrome.

Explicitly ruled out: neon, vaporwave, synthwave, purple/pink gradients, glowing text, chrome
lettering, grid horizons. Depth comes from flat panels, hairline bevels and procedurally-drawn
brushed grain. This look stays on macOS; do not port it to Windows.

### Windows: Windows 11 Fluent

The Windows app looks like a first-party Windows 11 app, not like the Mac one.
`windows/src/Murmur.App/Design/` holds it: `DesignTokens.cs` (sizes, type, motion),
`ThemePalette.cs` (colours per theme), `ThemeResources.cs` (puts them in the application's
theme dictionaries), `AccentPolicy.cs`, `Icons.cs`.

- **Fluent controls, drawn by FluentAvalonia.** NavigationView for the hub's sidebar,
  SettingsExpander cards in groups for Settings, ToggleSwitch for every on/off (there are no
  check boxes), ComboBox or the app's `SegmentedControl` for choices — never a row of buttons
  standing in for one. Segoe UI Variable through the theme's system font (Inter is the embedded
  fallback the headless previews draw with), 14 px body, Windows 11's radii: 4 on controls, 8
  on cards.
- **Mica and acrylic are system materials, not gradients.** The hub asks for Mica, then
  acrylic, then nothing, and paints a solid canvas when nothing is granted. They are composed by
  DWM and cost the app no frames. High contrast always gets solid surfaces.
- **Colours are live resources.** Views bind with `Tokens.Live.*`
  (`[!TextBlock.ForegroundProperty] = Tokens.Live.Ink`), so light, dark and high contrast switch
  with the window open. Assigning `Tokens.Brushes.*` freezes a control in the palette it was
  built in; that is for self-drawing controls, which repaint through `ThemeAware`.
- **The accent follows Windows — except red.** `AccentPolicy` swaps a red-ish accent (hue
  within 340–20°, saturation above 0.4) for Murmur's indigo `#5B5BD6`, which is also the
  fallback with no system accent. High contrast uses its own cyan. Change the band only on
  purpose, and change `AccentPolicyTests` with it.
- **The floating bar is dark in every theme**: a capsule at 92 % opacity with a hairline and one
  soft shadow, no blur. Level bars only while recording, the word "Transcribing" (no spinner).
  Its only transitions are opacity fades of 150 ms or less; colours change at once, nothing
  loops, and the window is resized once per state change, never per frame. `IdleCostTests` hold this.
- **Icons are Fluent System Icons** (MIT, credited in `THIRD-PARTY-NOTICES.md`) as path data in
  `Icons.cs`, not font glyphs. Take a new one from the SVG of the same name and keep the `F1`
  prefix; `IconTests` parse every one.
- **The privacy badge is never red.** Beside the bar's dot: a laptop (Parakeet, local), a cloud
  (Azure), a struck-out cloud after the offline fallback answered — always in the bar's muted ink.
  Notices after a fallback or a lost dictation ("Azure unreachable — used Parakeet") replace the
  label at rest for four seconds on a one-shot timer; a lost dictation lights the dot in the notice
  amber, a fallback does not. Hands-free shows the level bars plus "Hands-free — tap to stop".
- **Previews are the review.** `PreviewRenderTests` render every hub page, every Settings group,
  every onboarding step and every bar state in light, dark and high contrast to
  `windows/artifacts/ui-preview/`. Look at them after a visual change.

---

## macOS specifics

**Code signing is load-bearing, not cosmetic.** TCC stores a code-signing *requirement* per
entry, not just a path. An ad-hoc signature changes every build, so the rebuilt binary stops
satisfying the stored requirement — and the symptom lies: the Accessibility toggle still
shows as **on** while the app is untrusted. The `Makefile` auto-detects a Developer ID via
`security find-identity`. Don't replace that with `--sign -`.

If a grant does get wedged, reset that one row — never toggle, and never omit the bundle ID:

```bash
tccutil reset Accessibility ai.pivotstudio.murmur-youtube
```

A bare `tccutil reset Accessibility` wipes every app on the machine. Then quit System
Settings entirely (⌘Q) before reopening; the Privacy pane caches its list.

**`log` may be shadowed in the user's shell.** Use `/usr/bin/log` explicitly.

**Don't run the `.app` from the repo folder.** It's iCloud-synced and the sync engine can
corrupt the signature. `make install` puts the running copy in `/Applications`.

---

## Windows specifics

The specifics below were expensive to establish and several were found the hard way. Treat
them as load-bearing. Full detail in `windows/README.md` and `docs/PARAKEET-WINDOWS.md`.

**Pinned versions that break silently at "latest":**

| Package | Pin | Why |
|---|---|---|
| `NAudio` | 2.3.0 | 3.x targets .NET 9+ and will not restore |
| `Avalonia.*` | 12.1.4, every package together | 12 lets the render loop sleep when idle; one 11.x assembly in a 12 app builds and then throws `MissingMethodException`. `PlatformTests` checks the app references one major |
| `xunit.v3` | 3.2.2 | `Avalonia.Headless.XUnit` 12 is built on the 3.x extensibility API; 4.x is a new major. Test projects must be `OutputType Exe` |
| `FluentAvaloniaUI` | 3.1.0 | Needs Avalonia ≥ 12.1. Its theme is installed from `Design/AppTheme.cs` (app and tests share it), with the user accent and system font off |
| `org.k2fsa.sherpa.onnx` | 1.13.5 | Bundles ONNX Runtime — never also reference `Microsoft.ML.OnnxRuntime` |

**xUnit v3's analyzer wants `TestContext.Current.CancellationToken`** on every call that takes a
token (xUnit1051, an error here). Pass it rather than suppressing the rule.

**Clipboard writes from the injector are transient.** `AvaloniaClipboardService` adds the three
registered formats Windows reads to keep a copy out of Win+V history and cloud sync. The user's
own Copy button in the history list writes plain text and does land in history, on purpose.

**Right Alt is AltGr** on German, Polish, UK, Nordic and most Latin-American layouts. Binding
push-to-talk there — and especially suppressing it — breaks typing `@`, `€`, `\`, `|` for
those users. Default is **Right Ctrl**, and the hook **observes without swallowing**: if the
key-down is swallowed and the key-up escapes, the target app believes Ctrl is held forever.

**The one exception is Esc, and only mid-dictation.** `ICancelKeySource`: the engine arms it while
it is recording or transcribing and disarms it at rest (`DictationEngine.SetState`); the hook only
obeys the flag. An armed Esc press is consumed as a pair — the key-down decides, and its key-up
(and auto-repeats) are swallowed with it even if the flag has changed since — so cancelling never
also closes the dialog the user was dictating into, and an unarmed press is never touched. Esc is
not a modifier, so a lost key-up cannot leave anything stuck. Don't widen this to any other key.

**Hands-free is a gesture in Core, not in the hook.** `PushToTalkGesture` sits between the hook and
the engine: a hold works as before, a tap (< 300 ms) followed by a second press within 300 ms
latches recording, the next press stops it, and a lone tap is released as the short hold it was
once the window closes (transcribed, not thrown away). Its one timer is armed only between a tap
and the second press; at rest nothing is scheduled. The engine resets it whenever it comes to
rest, so a latch never outlives its recording — but a plain hold survives the reset (it may be a
new press queued behind the engine's gate) and its release is always passed on. Esc is disarmed
once transcription has produced text, so it never interrupts delivery or a paste in flight.

**The paste-last shortcut is unbound by default and registered under id `0xBFFE`.** A global
shortcut is taken from every app, and the obvious one (Ctrl+Alt+V) is Paste Special in Office, so
none is taken until the user records one. Saved prompts count up from 1 and probe under `0xBFFF`;
a prompt that already holds the same combination keeps it, and each side reports the clash as
Murmur's own, never "another app". Every shortcut recorder suspends *all* of Murmur's
registrations (`ShortcutSuspension.All`), or Windows would swallow the other owner's combination
and fire it into the recorder. The tray and the bar menu *copy* the last transcript rather
than paste it — clicking either takes focus from the window the text was for.

**UI Automation cannot inject text.** `TextPattern` is documented read-only and
`ValuePattern` replaces a whole field rather than inserting at the caret. `SendInput` is the
primary path, not a fallback.

**`Murmur.App` loads the platform layer by reflection, not by reference.** A direct
reference would force the UI onto `net10.0-windows` and you would lose the ability to run it
on your own machine. Two consequences that have already bitten once: the assembly is
invisible to `PublishSingleFile`, so it is published as a loose file beside the exe *and*
resolved by an explicit `AssemblyLoadContext` handler; and the published self-test checks
this, because when it breaks the app starts perfectly and then does nothing at all when the
key is pressed.

**Keep `Murmur.Platform.Windows` logic-free.** Anything living there is code CI cannot
exercise. Retries, debouncing and device-change handling belong in the platform-neutral
projects behind an interface — those target plain `net10.0`, so `CA1416` turns any accidental
Win32 call into a build error.

**Cloud code lives in `Murmur.Cloud`, not the platform layer.** It is plain `net10.0`,
references `Murmur.Abstractions` only, and is an ordinary `ProjectReference` of `Murmur.App`
(unlike the platform layer), so it is bundled into the single-file exe. The only Windows piece
of the cloud feature is `DpapiDataProtector` — two P/Invokes — and where the key file lives
and what happens when it cannot be decrypted is `Murmur.Core.ProtectedSecretFile`. Retries are
hand-written in `AzureTranscriptionClient`, not Polly: the policy is small and its tests pin
every status code. `HttpClient.Timeout` is infinite on purpose; the client enforces its own
budget, and a second timeout underneath would report itself as a user cancellation.

**CI is the only place the Windows code is compiled.** Warnings are errors and the analyzers
are strict on purpose. `--no-incremental` is mandatory: Roslyn does not re-emit analyzer
warnings on a cached build, so without it the gate proves nothing.

---

## Regex, if you touch the dictionary

The two engines are not identical. Measured across 30 cases, **9 diverged**. Two affect this
code and are handled — don't remove either:

- `RegexOptions.CultureInvariant` on the C# side, or Turkish `İ` matches `i`.
- **NFC normalization on both sides.** macOS returns decomposed strings, so without it an
  accented trigger silently never fires.

Two more are unfixable and simply avoided: ICU folds `ß` to `ss` and .NET doesn't; .NET's `.`
splits surrogate pairs. Stay inside the safe subset — `\b`, `\d`, `\w`, `\s`, character
classes, greedy/lazy quantifiers, alternation, `(?<name>…)`, fixed-length lookbehind,
lookahead, `\p{L}`, and `$1`–`$9` in replacements. Nothing else.

---

## What isn't built

1. **Command Mode** — select text, hold a second key, "make this more formal."
2. **Onboarding on macOS** — a first-run window walking through the macOS permissions. (Windows
   has one: `OnboardingWindow`, shown to new installs only — `Onboarding.IsNeeded` reads
   `SettingsVersion`, and a settings file without that field is an existing user.)
3. **Notarization** (macOS) and **code signing** (Windows). Both apps are unsigned for
   distribution, so Windows users will meet SmartScreen.
4. **An installer** for Windows, and model download from inside the app rather than by
   following `docs/PARAKEET-WINDOWS.md` by hand.

## What no amount of CI can verify

On Windows, nobody has yet held the key and spoken. Specifically unverified:

- Text injection landing in a foreground app — runners have an interactive desktop but
  cannot take the foreground.
- A real microphone: format negotiation, the OS privacy block, unplugging mid-capture.
- The keyboard hook firing on a physical keypress.
- Parakeet transcribing real speech, and whether ~2 GB resident is tolerable.
- A real Azure OpenAI resource answering. Tests use a stub handler and never touch a network;
  Settings → Test connection is the first check on a new setup.

Everything those feed into is behind an interface and tested with fakes. The bindings
themselves are not.

**On a Windows PC with the model installed, `tests/Murmur.E2E.Tests` closes part of this gap**
(opt-in, `MURMUR_E2E=1`; see `docs/TESTING-LOCAL.md`): spoken clips through the real pipeline and
real Parakeet, the real Azure client against a stand-in endpoint on `127.0.0.1` that answers with
real transcriptions, and the published exe's idle CPU and memory in both engine modes. One small
production seam exists for it and is not to be widened: `MURMUR_DATA_DIR` (`MurmurPaths`) moves every
data file except the model. Plain http to its loopback stand-in is allowed only inside the test
process (internal `AzureEndpoint.AllowLoopbackHttp`); the app itself still requires https. **First real-hardware run should start with `--selftest`, then a single
short dictation into Notepad.**
