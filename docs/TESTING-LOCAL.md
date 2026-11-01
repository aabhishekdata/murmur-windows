# Testing on a real Windows PC

CI proves the Windows app with fakes. This suite proves it with **real speech**: spoken clips go
through the app's real dictation pipeline, the real Parakeet model, and the real Azure client over
real HTTP to a stand-in Azure endpoint on `127.0.0.1`. It runs unattended, needs no Azure account
and no microphone, and touches nothing of your own Murmur settings.

It is opt-in. Without `MURMUR_E2E=1` every test in it skips, so CI and an ordinary
`dotnet test Murmur.sln` are unaffected.

---

## What you need

- Windows with the .NET 10 SDK.
- The Parakeet model in `%LOCALAPPDATA%\Murmur\models\parakeet-v2` — see
  [PARAKEET-WINDOWS.md](PARAKEET-WINDOWS.md).
- A folder of spoken clips: 16 kHz mono PCM16 WAV files plus `expected.tsv`, one
  `name<TAB>spoken text` line per clip. The suite expects these six:

  | Clip | Spoken |
  |---|---|
  | `hello` | an ordinary sentence |
  | `sentence` | another |
  | `trigger` | "review prompt" (the saved prompt's trigger, said on its own) |
  | `trigger-enter` | "review prompt press enter" |
  | `filler` | a sentence with "um" and "uh" in it |
  | `long` | over 30 seconds of speech |

  Text-to-speech output is fine; that is how the reference set was made.
  `windows/tests/Murmur.E2E.Tests/make-clips.ps1 -OutputDirectory <folder>` regenerates exactly
  that set with Windows' built-in voice.

## Running it

```powershell
cd windows
$env:MURMUR_E2E = '1'
$env:MURMUR_E2E_AUDIO = 'C:\path\to\clips'      # the folder with expected.tsv (required, no default)
dotnet test tests/Murmur.E2E.Tests
```

About six minutes: the microphone stand-in plays each clip in real time, as a person would speak
it. Tests run one at a time on purpose, so no test's model load skews another's timings.

To include the published exe as well, publish it the way CI does, **outside the repo**, and name it:

```powershell
dotnet publish src/Murmur.App/Murmur.App.csproj --configuration Release --runtime win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishReadyToRun=true --output $env:TEMP\murmur-publish
$env:MURMUR_E2E_EXE = "$env:TEMP\murmur-publish\Murmur.App.exe"
dotnet test tests/Murmur.E2E.Tests --filter PublishedExeTests
```

Close Murmur first: it is single-instance, and the test refuses to run beside a copy of it.

Results land in `windows/artifacts/e2e/` (git-ignored): `latency.tsv`, one line per dictation, and
`idle.tsv`, the published exe's idle figures. Each run appends.

## What it proves

**The pipeline is the app's own.** `Pipeline` assembles `DictationEngine`, `RoutingTranscriber`,
`FallbackTranscriber`, `LazyTranscriber` around `ParakeetTranscriber`, `AzureOpenAITranscriber` over
the `HttpClient` from `AzureHttp`, `SmartTextInjector`, the text pipeline with a dictionary and a
multi-line saved prompt, `AppSettings`, and the key in a `ProtectedSecretFile` encrypted with the real
DPAPI protector — exactly as `Composition.Create` does, using its own helpers for the time limits and
key rejection. Only the edges of the operating system are replaced: the microphone plays a WAV file
in 50 ms chunks, the push-to-talk key is pressed by the test, and keystrokes and the clipboard are
recorded instead of reaching whatever window has focus.

**The stand-in endpoint behaves like Azure.** `MockAzureServer` (Kestrel, a random port on
`127.0.0.1`) checks the `api-key` header, the `api-version`, the deployment in the path and every
multipart field — that `file` really is PCM16 16 kHz mono WAV, `response_format=json`,
`chunking_strategy=auto` — and answers with a **real Parakeet transcription of the audio it was
sent**. Failures are scripted: 401, 403, 404, 429 with `retry-after-ms`, 5xx a set number of times,
a request that never answers, and 400 with Azure's `error.message`.

The app requires https for every endpoint, loopback included. The suite reaches its stand-in over
plain `http://127.0.0.1` by setting the internal `AzureEndpoint.AllowLoopbackHttp` for its own process
only; nothing in the app sets it. The published exe, which the suite cannot switch, is pointed at
`https://` on the stand-in's port, and the check that it sends nothing at rest counts TCP
connections, so even a TLS handshake would show.

| Area | Checked |
|---|---|
| Parakeet mode | Every clip transcribed within a 25 % word error rate, cold and warm; **no request reaches the endpoint**, although it is fully set up |
| Azure mode | Every clip on the Accurate deployment, then on Fast after the tray's quick switch with no restart; each request well-formed, carrying the key, the api-version and the dictionary terms in `prompt`; **Parakeet never loaded** |
| Saved prompt | "review prompt" pastes the multi-line prompt verbatim and types nothing; "… press enter" pastes, then presses Enter |
| Fillers | "um" and "uh" are gone from what is typed |
| Long audio | The 31 s clip, in both engines and through the fallback |
| Fallback mode | 503 on every attempt → Parakeet answers, `FellBack` is set, and the next dictation skips the cloud while the circuit is open; 503 twice then OK → Azure answers; 429 → waits as asked; no answer → times out after the configured 5 s and Parakeet answers; endpoint gone (connection refused) → Parakeet answers |
| Setup errors | 401 → reported, not retried, **no fallback**, key marked rejected, nothing typed; 403, 404 and 400 the same (but the key is not marked rejected), with Azure's message passed on; Azure-only mode never falls back |
| Language | Hindi sends `language=hi`, English `en`; Hinglish sends none and the romanised-Hindi hint |
| The key | Never on disk in the clear, in any file of the data folder; a fresh store over the same folder decrypts it |
| Published exe | `--selftest` exits 0; at rest for 60 s after a 20 s settle, in Parakeet mode and in Azure mode (pointed at the running stand-in, key saved through `ProtectedSecretFile`): processor time (at most 0.5 % of one core in both modes), private bytes (under 200 MB in Azure mode, and under half of Parakeet's), whether the speech runtime is loaded, and that no connection reaches the stand-in |

The exe is measured with a data folder of its own through **`MURMUR_DATA_DIR`**, which moves
settings, the key, history, snippets, the dictionary, recent audio and logs (`MurmurPaths`). The
model is still found in `%LOCALAPPDATA%\Murmur\models`. The suite sets the same variable for its own
process, so the engine's logs land in its scratch folder too.

## Results on the development PC (1 Nov 2026, 28 logical processors)

| Engine | Clip | Audio | Key release → text |
|---|---|---|---|
| Parakeet | hello | 3.6 s | 142–151 ms |
| Parakeet | long | 31.2 s | 1.17 s |
| Azure stand-in, Accurate / Fast | hello | 3.6 s | 154 / 150 ms |
| Azure stand-in, Accurate / Fast | long | 31.2 s | 1.19 s / 1.19 s |
| Fallback: 503 × 3 | sentence | 3.4 s | 3.4 s |
| Fallback: no answer, 5 s timeout | sentence | 3.4 s | 6.4 s |
| Fallback: connection refused | sentence | 3.4 s | 8.6 s |

| Published exe at rest | Processor | Private bytes | Speech runtime |
|---|---|---|---|
| Parakeet mode | 0.10 % of one core | 777 MB | loaded |
| Azure mode | 0.05 % of one core | 94 MB | not loaded |

Read the cloud numbers for what they are: the stand-in transcribes with Parakeet on this PC, so they
measure Murmur's own overhead around a cloud call — encoding, the multipart upload, parsing — and not
Azure's network round trip or its model. "Cold" and "warm" Parakeet are the same because the model
loads when the key goes down and finishes while the clip is still playing.

## What it does not prove

- **A real Azure resource.** The request shape is checked against what the service documents, not
  against the service. Settings → Test connection, on a real key, is still the first check.
- **A real microphone, the keyboard hook, or text landing in another app.** Those are the
  platform layer's bindings; see "What no amount of CI can verify" in AGENTS.md.
- **The network going away as Windows reports it.** The test stops the endpoint, which refuses the
  connection; the app's `NetworkChange` subscription, which opens the circuit at once on a real
  disconnect, is not exercised. Without it each refused attempt costs about two seconds on Windows,
  which is where the 8.6 s above comes from.
