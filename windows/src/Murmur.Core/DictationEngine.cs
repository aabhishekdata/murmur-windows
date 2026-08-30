using System.Buffers;
using Murmur.Abstractions;
using Murmur.Dictionary;

namespace Murmur.Core;

/// <summary>What the engine is doing right now.</summary>
public enum DictationState
{
    /// <summary>Waiting for the hotkey.</summary>
    Idle,

    /// <summary>The key is held; audio is being captured.</summary>
    Recording,

    /// <summary>The key is released; the utterance is being transcribed.</summary>
    Transcribing,
}

/// <summary>One completed dictation.</summary>
public sealed record DictationResult(
    DateTimeOffset At,
    TimeSpan AudioDuration,
    TimeSpan ProcessingTime,
    string Text,
    IReadOnlyList<AppliedCorrection> Corrections);

/// <summary>
/// The whole dictation flow: hotkey down, capture, hotkey up, transcribe, correct, inject.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately platform-neutral. It targets plain <c>net10.0</c>, so <c>CA1416</c> turns any
/// accidental Windows API call in here into a build error. Everything platform-specific
/// arrives through the four interfaces it is constructed with.
/// </para>
/// <para>
/// That is what makes the interesting behaviour testable without Windows: hand it fakes and
/// the entire path — including chunking, the correction pass and the "nothing was said"
/// case — runs on any machine, in milliseconds.
/// </para>
/// </remarks>
public sealed class DictationEngine : IAsyncDisposable
{
    private readonly IAudioCapture _capture;
    private readonly IHotkeySource _hotkey;
    private readonly ITranscriber _transcriber;
    private readonly ITextInjector _injector;
    private readonly IClock _clock;
    private readonly Func<IReadOnlyList<DictionaryEntry>> _dictionary;
    private readonly Func<TextPipelineOptions> _options;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _recording;
    private List<float>? _buffer;
    private DateTimeOffset _startedAt;

    /// <summary>Current state.</summary>
    public DictationState State { get; private set; } = DictationState.Idle;

    /// <summary>Most recent input level, 0…1. Drives the meter.</summary>
    public float Level { get; private set; }

    /// <summary>Raised when a dictation completes and produced text.</summary>
    public event EventHandler<DictationResult>? Completed;

    /// <summary>Raised whenever <see cref="State"/> or <see cref="Level"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised when audio capture fails with anything other than cancellation.
    /// </summary>
    /// <remarks>
    /// <see cref="BeginAsync"/> runs fire-and-forget, so an exception thrown by the capture
    /// implementation (e.g. the microphone refusing every format) would otherwise vanish as
    /// an unobserved task exception — the meter simply never moves and nothing is reported.
    /// The same failure is also appended to <c>%LocalAppData%/Murmur/last-capture-error.log</c>
    /// so it can be inspected after the fact.
    /// </remarks>
    public event EventHandler<Exception>? CaptureFaulted;

    /// <summary>Wires the engine to its platform implementations.</summary>
    /// <param name="capture">Microphone source.</param>
    /// <param name="hotkey">Push-to-talk source.</param>
    /// <param name="transcriber">Speech engine.</param>
    /// <param name="injector">Where finished text goes.</param>
    /// <param name="dictionary">
    /// Read fresh on every utterance rather than captured once, so edits take effect without
    /// a restart.
    /// </param>
    /// <param name="clock">Time source; defaults to the system clock.</param>
    /// <param name="options">
    /// Which text passes to run, read fresh on every utterance for the same reason the
    /// dictionary is — a setting toggled in the Hub takes effect on the next dictation rather
    /// than after a restart. Defaults to everything on.
    /// </param>
    public DictationEngine(
        IAudioCapture capture,
        IHotkeySource hotkey,
        ITranscriber transcriber,
        ITextInjector injector,
        Func<IReadOnlyList<DictionaryEntry>> dictionary,
        IClock? clock = null,
        Func<TextPipelineOptions>? options = null)
    {
        _capture = capture;
        _hotkey = hotkey;
        _transcriber = transcriber;
        _injector = injector;
        _dictionary = dictionary;
        _options = options ?? (static () => new TextPipelineOptions());
        _clock = clock ?? SystemClock.Instance;

        _hotkey.Pressed += OnPressed;
        _hotkey.Released += OnReleased;
    }

    /// <summary>Arms the hotkey.</summary>
    /// <returns>False if the hook could not be installed.</returns>
    public bool Start() => _hotkey.Start();

    /// <summary>
    /// Starts or stops recording from a button rather than the hotkey.
    /// </summary>
    /// <remarks>
    /// Routed through the same state machine as the hotkey, deliberately. Two independent
    /// paths into recording would eventually disagree about whether it is running.
    /// </remarks>
    public void TogglePushToTalk()
    {
        if (State == DictationState.Idle) _ = BeginAsync();
        else if (State == DictationState.Recording) _ = EndAsync();
    }

    private void OnPressed(object? sender, EventArgs e) => _ = BeginAsync();

    private void OnReleased(object? sender, EventArgs e) => _ = EndAsync();

    private async Task BeginAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DictationState.Idle) return;

            _buffer = [];
            _startedAt = _clock.Now;
            _recording = new CancellationTokenSource();
            SetState(DictationState.Recording);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await foreach (var chunk in _capture.CaptureAsync(_recording!.Token).ConfigureAwait(false))
            {
                // Stop consuming the moment recording ends. Cancellation is cooperative, so
                // chunks already queued still arrive after EndAsync has moved on — and
                // without this guard one of them sets Level back to a reading that has
                // already been zeroed.
                if (State != DictationState.Recording) break;

                // Copied, not referenced: capture implementations are entitled to reuse
                // their buffer the moment this returns.
                _buffer?.AddRange(chunk.Samples.Span);
                Level = chunk.Rms();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: the key was released.
        }
        catch (Exception ex)
        {
            // Capture failed outright (e.g. no format the device accepts). Without this the
            // exception would be swallowed as an unobserved task fault and the app would look
            // merely inert. Log first so the failure is recorded even if no one subscribes.
            LogCaptureError(ex);
            CaptureFaulted?.Invoke(this, ex);
        }
        finally
        {
            // Authoritative: this runs only once the capture loop has genuinely finished, so
            // nothing can raise the level afterwards and leave the meter stuck.
            Level = 0;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task EndAsync()
    {
        List<float>? samples;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DictationState.Recording) return;

            await _recording!.CancelAsync().ConfigureAwait(false);
            samples = _buffer;
            _buffer = null;
            Level = 0;
            SetState(DictationState.Transcribing);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await ProcessAsync(samples).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // EndAsync runs fire-and-forget off the hotkey thread, so nothing above this
            // frame ever observes the task. Without this catch, any failure in the
            // transcribe → complete → inject pipeline becomes an unobserved task exception:
            // the app looks merely inert, exactly like the shipped bug where a UI-thread
            // violation in a Completed subscriber silently blocked injection. Log it so the
            // whole class of "completion died silently" failures leaves a trace on disk.
            LogPipelineError("Dictation processing failed", ex);
        }
        finally
        {
            _recording?.Dispose();
            _recording = null;
            SetState(DictationState.Idle);
        }
    }

    private async Task ProcessAsync(List<float>? samples)
    {
        if (samples is null || samples.Count == 0) return;

        // Measured from key release, because that is the wait the user actually feels — and
        // it is the only figure on which a streaming and a batch engine compare honestly.
        var releasedAt = _clock.Now;
        var audio = new ReadOnlyMemory<float>(samples.ToArray());

        var entries = _dictionary();
        var bias = DictionaryCorrector.BiasPhrases(entries);

        var pieces = AudioSegmenter.Split(audio);
        var transcripts = new List<string>(pieces.Count);

        foreach (var piece in pieces)
        {
            var text = await _transcriber
                .TranscribeAsync(piece, bias, CancellationToken.None)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(text)) transcripts.Add(text.Trim());
        }

        var raw = string.Join(' ', transcripts);
        if (string.IsNullOrWhiteSpace(raw)) return;

        // Everything that happens to the text between the recogniser and the keyboard lives in
        // TextPipeline: filler removal, the dictionary, spoken retractions, the trailing
        // command, snippet expansion and list formatting, in that order and for the reasons
        // documented there. All of it is deterministic pattern matching — this pipeline
        // contains no language model and must not acquire one. It runs before injection so the
        // typed text and the saved transcript are the same string.
        var processed = TextPipeline.Run(raw, _options() with
        {
            Dictionary = entries,
        });

        var formatted = processed.Text;

        // The whole utterance was hesitation, or a bare "press enter" with nothing to type.
        // Empty text must never be injected, and an empty transcript is not worth recording.
        if (string.IsNullOrWhiteSpace(formatted))
        {
            if (processed.Submit) await SubmitAsync().ConfigureAwait(false);
            return;
        }

        var result = new DictationResult(
            At: releasedAt,
            AudioDuration: TimeSpan.FromSeconds((double)audio.Length / AudioChunk.SampleRate),
            ProcessingTime: _clock.Now - releasedAt,
            Text: formatted,
            Corrections: processed.Corrections);

        // Subscribers run synchronously on this background thread, and injection must not
        // depend on any of them succeeding. This ordering (Completed first) is deliberate —
        // the history subscriber persists the text, so even a failing injector never loses
        // a dictation — but a subscriber failure (e.g. a UI-thread violation) must equally
        // never block the text from reaching the focused app, hence the isolation here.
        try
        {
            Completed?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            LogPipelineError("Completed subscriber failed", ex);
        }

        await _injector.InjectAsync(formatted, CancellationToken.None).ConfigureAwait(false);

        // Enter goes last, after the text has landed. Ordering matters here in a way that is
        // easy to get wrong: submitting first would send an empty message.
        if (processed.Submit) await SubmitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Presses Enter in the focused application.
    /// </summary>
    /// <remarks>
    /// Isolated, because the keystroke is a nice-to-have on top of a dictation that has already
    /// succeeded. An injector that cannot deliver a keypress must not turn a completed
    /// dictation into a logged failure.
    /// </remarks>
    private async Task SubmitAsync()
    {
        try
        {
            await _injector.PressEnterAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPipelineError("Pressing Enter failed", ex);
        }
    }

    /// <summary>Appends a capture failure to a small diagnostic log.</summary>
    /// <remarks>
    /// The app is a WinExe with no console, so a file is the only way to inspect the failure
    /// from outside a running instance. Best-effort by design: a logging problem must never
    /// crash the engine or mask the original error, so any write failure is swallowed.
    /// </remarks>
    private static void LogCaptureError(Exception ex) =>
        AppendLog("last-capture-error.log", ex.ToString());

    /// <summary>Appends a completion-pipeline failure to the crash log.</summary>
    /// <remarks>
    /// Same file and format as the app layer's <c>CrashLog</c>, deliberately: a failure here
    /// means "a dictation died after the key was released", which is an app-broke event, not
    /// a per-recording capture problem — those go to <c>last-capture-error.log</c>.
    /// </remarks>
    private static void LogPipelineError(string context, Exception ex) =>
        AppendLog("last-crash.log", $"{context}: {ex}");

    private static void AppendLog(string fileName, string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Murmur");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, fileName),
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Never let logging failure hide the real problem.
        }
    }

    private void SetState(DictationState state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _hotkey.Pressed -= OnPressed;
        _hotkey.Released -= OnReleased;
        _hotkey.Dispose();

        if (_recording is not null)
        {
            await _recording.CancelAsync().ConfigureAwait(false);
            _recording.Dispose();
        }

        await _capture.DisposeAsync().ConfigureAwait(false);
        await _transcriber.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
