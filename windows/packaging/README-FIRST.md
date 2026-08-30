# Murmur for Windows — setup

Built from https://github.com/per-simmons/murmur-youtube (windows/, win-x64, self-contained — no .NET install needed).

## 1. Unzip everything to one folder
Keep `Murmur.App.exe`, `Murmur.Platform.Windows.dll` and the `NAudio*.dll` files together — the platform layer is loaded from beside the exe at startup. Avoid `C:\Program Files` (the app can't write there without admin rights).

## 2. Download the speech model (one time, ~661 MB)
Windows has no built-in speech engine the app can use, so it cannot transcribe until the Parakeet model is on disk. Right-click `get-model.ps1` → Run with PowerShell (or run `powershell -ExecutionPolicy Bypass -File .\get-model.ps1`). It downloads to `%LOCALAPPDATA%\Murmur\models\parakeet-v2`.

If you dictate in a language other than English, edit the script: `parakeet-v2` → `parakeet-v3` and `-v2-int8` → `-v3-int8` (25 languages).

## 3. Run it
Double-click `Murmur.App.exe`. Windows SmartScreen may warn because the exe is unsigned — click "More info" → "Run anyway". Allow microphone access when asked (Settings → Privacy → Microphone if it's blocked).

Hold **Right Ctrl**, speak, release — the cleaned-up text lands in the active text field.

## The floating bar
A small dark pill appears at the bottom of the screen. It floats above other windows, never takes focus, and shows what Murmur is doing:

- **at rest** — a dim dot and a row of small bars
- **listening** — a red dot and white bars that move with your voice, plus a ✕ to stop
- **transcribing** — three pulsing dots
- **"No model"** — an amber dot, meaning the download in step 2 hasn't happened yet

**Right-click** it for the menu: open Murmur, transcript history, which microphone to use, which edge to dock to, hide the bar, quit.

**Click** the bar to start and stop dictating (the hotkey does the same thing and is still the main way to use it). **Drag** it anywhere: it settles onto the nearest edge — bottom, left or right — and turns vertical on a side. Where you leave it is remembered between launches.

If you hide the bar, the hotkey keeps working; the tray icon → "Show floating bar" brings it back.

## The main window
Right-click the bar → **Open Murmur**, or the tray icon → **Open Murmur**. Four sections down the left:

- **Home** — words dictated, recordings, speaking rate and day streak, suggested corrections, then your transcript history grouped by day and searchable. Hover a row for Teach, Copy and Delete.
- **Dictionary** — words the recogniser keeps getting wrong, and the spellings you want instead.
- **Snippets** — spoken phrases that expand into stored blocks of text.
- **Settings** — push-to-talk key, microphone, model status, which edge the bar docks to, the speech clean-up switches, and whether transcripts are typed and kept.

Closing this window leaves Murmur running in the tray. Quit is explicit, from the tray or the bar's menu.

## What Murmur does to your words
Five clean-up passes run on the transcript before it is typed. Every one is pattern matching — there is no language model anywhere in Murmur, nothing you say leaves the machine, and the same words always produce the same text. Each has its own switch in **Settings → Clean up what I say**, and all of them start on.

| You say | Murmur types |
|---|---|
| "So, um, I think we should uh ship it." | So, I think we should ship it. |
| "Let's meet Tuesday, wait no, Friday." | Let's meet Friday. |
| "One, milk. Two, eggs. Three, bread." | 1. Milk / 2. Eggs / 3. Bread (on separate lines) |
| "Here's what I need: milk, eggs, bread" | a bulleted list under the lead-in |
| "Tell Dave I'm running late. Press enter." | Tell Dave I'm running late. — then presses Enter |

All of them are deliberately cautious, and when they are unsure they do nothing:

- **Filler words** are only removed as whole words. "Ahmed", "German", "umbrella", "ah-ha" and "uh-huh" are safe.
- **Spoken corrections** only fire on "wait no", "actually no", "scratch that", "I mean", "I meant" and "sorry I meant", and only when the shape is unmistakable. A retraction never removes more words than it adds. "I can't wait, no really" and "I meant to call you" are left alone.
- **Lists** need real counting — "the first time I met her" and "I only have one question" are untouched.
- **Press Enter** only fires when the phrase is a sentence of its own at the very end. "To submit the form, press enter" is typed, not obeyed.

## Snippets
**Snippets** in the main window stores a block of text behind a spoken phrase — an address, a signature, a standup update, a standard reply. Say the phrase anywhere in a dictation and the whole block gets typed. Up to 4,000 characters each, and they can span several lines.

Pick a trigger of two or three uncommon words. Murmur warns you if you pick something it will fire on constantly, and ignores triggers under three characters.

## The dictionary, and what it learns
**Dictionary** holds the words Murmur keeps getting wrong and the spellings you want instead. Two things help you fill it in:

- **Suggestions on Home.** When a transcript contains something that is nearly one of your dictionary words but not quite — "cloud code" when you have "Claude Code" — Murmur offers a correction. Nothing is applied until you accept it, accepting one creates an ordinary entry you can edit or delete, and turning one down is remembered.
- **Teach.** Hover any transcript and click **Teach** to turn a mistake you spotted yourself into a rule. It tells you how many past transcripts the rule would have fixed, which is the quickest way to catch a typo in it.

Murmur cannot watch you fix a word in another application — it types into other apps but never reads from them, which is a deliberate privacy choice — so these two are the honest version of "it learns".

## Choosing a microphone
Right-click the bar → **Microphone**, or **Settings → Microphone**. A microphone you pick by hand overrides both of Windows' defaults (Windows keeps a separate "default" and "default communications" device, and they often disagree). If the one you picked is unplugged, Murmur falls back to the system default rather than failing to record.

## Notes
- Expect ~2 GB of RAM used while the model is loaded; transcription is fully offline and ~40× faster than real time on CPU.
- The upstream project marks this build "feature-complete, never run on real hardware" — you may be the first human to actually dictate into it, so treat rough edges as expected.
- To sanity-check the install, run `Murmur.App.exe --selftest` in a terminal; exit code 0 means the bundle, JSON round-trip and platform layer all load correctly.
