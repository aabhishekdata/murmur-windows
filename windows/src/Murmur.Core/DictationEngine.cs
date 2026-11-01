using System.Globalization;
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
    IReadOnlyList<AppliedCorrection> Corrections)
{
    /// <summary>Identity, shared with the history record and the audio kept for a retry.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The recording itself: 16 kHz mono float, exactly as transcribed.</summary>
    /// <remarks>
    /// Handed on so the app can keep the last few for "Retry with the other engine". Held in
    /// memory only; nothing in the engine writes audio anywhere.
    /// </remarks>
    public ReadOnlyMemory<float> Audio { get; init; }

    /// <summary>The engine that heard it.</summary>
    /// <remarks>
    /// For audio long enough to be split, the engine that heard the last piece. Unknown when
    /// the transcriber does not say, as test doubles mostly do not.
    /// </remarks>
    public TranscriberKind Engine { get; init; }

    /// <summary>Whether any of it was heard by the fallback rather than the chosen engine.</summary>
    public bool FellBack { get; init; }
}

/// <summary>Time limits for <see cref="DictationEngine"/>.</summary>
/// <remarks>
/// Before these existed every call passed <see cref="CancellationToken.None"/>, so a
/// transcriber that never answered left the engine in
/// <see cref="DictationState.Transcribing"/> for the life of the process — the bar stuck on
/// its spinner, and the key dead, because a new recording only starts from idle.
/// </remarks>
public sealed record DictationEngineOptions
{
    /// <summary>The part of the transcription budget that does not depend on length.</summary>
    /// <remarks>Covers a cold model load or a slow network round trip on a short utterance.</remarks>
    public TimeSpan TranscriptionBaseBudget { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>More fixed budget, asked for at the start of each utterance; null adds none.</summary>
    /// <remarks>
    /// For an engine whose own timeout is longer than <see cref="TranscriptionBaseBudget"/>, and
    /// may change while the app runs: the cloud's request timeout, in the cloud modes only.
    /// </remarks>
    public Func<TimeSpan>? ExtraBaseBudget { get; init; }

    /// <summary>Seconds of budget added per second of audio.</summary>
    /// <remarks>
    /// Two: Parakeet runs some forty times faster than real time and a cloud model several
    /// times faster, so anything slower than half real time has stopped working rather than
    /// merely being slow.
    /// </remarks>
    public double TranscriptionBudgetPerAudioSecond { get; init; } = 2;

    /// <summary>How long typing the text, or pressing Enter, may take.</summary>
    public TimeSpan InjectionTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Drives every timeout; replaced in tests.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How long transcribing <paramref name="audio"/> may take in total.</summary>
    /// <param name="audio">Length of the utterance.</param>
    /// <returns>The base budget, any extra, and the per-second allowance.</returns>
    public TimeSpan TranscriptionBudget(TimeSpan audio) =>
        TranscriptionBaseBudget + (ExtraBaseBudget?.Invoke() ?? TimeSpan.Zero) + audio * TranscriptionBudgetPerAudioSecond;
}

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
    private readonly Func<bool> _injectText;
    private readonly DictationEngineOptions _limits;
    private readonly PushToTalkGesture? _gesture;
    private readonly ICancelKeySource? _cancelKey;

    /// <summary>Cancelled on dispose. Every token the engine hands out is linked to it.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _recording;
    private SampleBuffer? _buffer;

    /// <summary>
    /// Guards <see cref="_buffer"/> between the capture loop and key release.
    /// </summary>
    /// <remarks>
    /// Cancellation is cooperative, so a chunk can still be mid-append when the key comes up.
    /// With a plain list that was merely a race; with pooled storage it would be a write into
    /// an array already handed back to the pool and lent to someone else.
    /// </remarks>
    private readonly Lock _bufferLock = new();
    private DateTimeOffset _startedAt;

    /// <summary>The utterance being transcribed, so Esc can abandon it; null otherwise.</summary>
    private CancellationTokenSource? _processing;

    /// <summary>Esc arrived between key release and <see cref="_processing"/> being set.</summary>
    private bool _cancelPending;

    /// <summary>
    /// Whether Esc may still abandon the dictation being transcribed. Set at key release, cleared
    /// once there is text to deliver and when the engine comes to rest. Read and written only
    /// under <see cref="_processingLock"/>, so an Esc either lands while it is true or not at all.
    /// </summary>
    private bool _cancellable;
    private readonly Lock _processingLock = new();

    /// <summary>Current state.</summary>
    public DictationState State { get; private set; } = DictationState.Idle;

    /// <summary>Most recent input level, 0…1. Drives the meter.</summary>
    public float Level { get; private set; }

    /// <summary>Raised when a dictation completes and produced text.</summary>
    public event EventHandler<DictationResult>? Completed;

    /// <summary>Raised whenever <see cref="State"/> or <see cref="Level"/> changes.</summary>
    /// <remarks>
    /// Fires at capture-buffer rate while recording, on a background thread. A view should
    /// not marshal every one of these; it subscribes to <see cref="StateChanged"/> and reads
    /// <see cref="Level"/> on its own frame clock.
    /// </remarks>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised when <see cref="State"/> moves to a different value, and at no other time.
    /// </summary>
    /// <remarks>
    /// Three or four events per dictation, on whichever background thread made the move. This
    /// is what the floating bar is driven by instead of a timer that polled forever.
    /// </remarks>
    public event EventHandler<DictationState>? StateChanged;

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

    /// <summary>
    /// Raised when an utterance could not be transcribed, on the processing thread.
    /// </summary>
    /// <remarks>
    /// Carries a <see cref="TranscriptionException"/> for the failures a user can act on — no
    /// model, a rejected key, the time budget running out — and whatever was thrown otherwise.
    /// Not raised when the engine is being disposed. The engine is back at
    /// <see cref="DictationState.Idle"/> by the time anyone could act on it.
    /// </remarks>
    public event EventHandler<Exception>? TranscriptionFaulted;

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
    /// <param name="injectText">
    /// Whether finished text is typed into the focused app, read per utterance. When it
    /// answers false the dictation still completes and is recorded — only the keystrokes,
    /// including a spoken "press enter", are withheld. Defaults to always.
    /// </param>
    /// <param name="limits">Time limits; defaults to <see cref="DictationEngineOptions"/>' own.</param>
    public DictationEngine(
        IAudioCapture capture,
        IHotkeySource hotkey,
        ITranscriber transcriber,
        ITextInjector injector,
        Func<IReadOnlyList<DictionaryEntry>> dictionary,
        IClock? clock = null,
        Func<TextPipelineOptions>? options = null,
        Func<bool>? injectText = null,
        DictationEngineOptions? limits = null)
    {
        _capture = capture;
        _hotkey = hotkey;
        _transcriber = transcriber;
        _injector = injector;
        _dictionary = dictionary;
        _options = options ?? (static () => new TextPipelineOptions());
        _injectText = injectText ?? (static () => true);
        _clock = clock ?? SystemClock.Instance;
        _limits = limits ?? new DictationEngineOptions();

        _hotkey.Pressed += OnPressed;
        _hotkey.Released += OnReleased;

        // Hands-free and Esc. Optional: a plain key source has neither, and the engine behaves
        // exactly as it always did.
        _gesture = hotkey as PushToTalkGesture;

        _cancelKey = hotkey as ICancelKeySource;
        if (_cancelKey is not null) _cancelKey.CancelPressed += OnCancelPressed;
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

    /// <summary>Watches a different push-to-talk key from now on.</summary>
    /// <param name="virtualKey">Windows virtual-key code.</param>
    /// <returns>Completes once any recording in progress has been finished.</returns>
    /// <remarks>
    /// A recording in progress is ended first, through the normal release path, so what was
    /// already said is still transcribed. Swapping the key under a held press would otherwise
    /// strand the engine in <see cref="DictationState.Recording"/>: the release it is waiting
    /// for belongs to a key that is no longer being watched.
    /// </remarks>
    public async Task SetPushToTalkKeyAsync(int virtualKey)
    {
        if (_hotkey.VirtualKey == virtualKey) return;

        // Started before the key is swapped, awaited after: the new key is live at once
        // rather than only once the old recording has finished transcribing.
        var ending = State == DictationState.Recording ? EndAsync() : Task.CompletedTask;

        _hotkey.VirtualKey = virtualKey;

        await ending.ConfigureAwait(false);
    }

    /// <summary>
    /// Abandons a recording in progress: the audio is discarded and nothing is typed.
    /// </summary>
    /// <returns>
    /// Completes once the engine is back at <see cref="DictationState.Idle"/>, or at once when
    /// it was not recording.
    /// </returns>
    /// <remarks>
    /// <para>
    /// What a saved prompt's key combination does before it pastes. Pressing Ctrl+Alt+1 while
    /// holding the push-to-talk key means "paste this instead", and transcribing the half-sentence
    /// already captured would type it in front of the prompt.
    /// </para>
    /// <para>
    /// A dictation already being transcribed is left alone: its words were finished, and the
    /// user released the key to send them. When the push-to-talk key is released after a
    /// cancel, the release finds the engine idle and does nothing.
    /// </para>
    /// </remarks>
    public Task CancelAsync() => CancelAsync(includeTranscription: false);

    /// <summary>
    /// Abandons the dictation in progress — recording, and optionally transcription too — so that
    /// nothing is typed and nothing is kept.
    /// </summary>
    /// <param name="includeTranscription">
    /// Whether a dictation already being transcribed is abandoned as well. True for Esc, which
    /// means "not that" whenever it is pressed; false for a saved prompt's shortcut, for the
    /// reason <see cref="CancelAsync()"/> gives.
    /// </param>
    /// <returns>Completes once the recording is discarded, or the transcription told to stop.</returns>
    /// <remarks>
    /// <para>
    /// An abandoned transcription unwinds by itself and the engine comes to rest once it has; the
    /// fallback engine is not asked, and neither <see cref="Completed"/> nor
    /// <see cref="TranscriptionFaulted"/> is raised for it.
    /// </para>
    /// <para>
    /// Only until there is text. From then on the dictation is delivered — kept in history, typed,
    /// Enter pressed — and Esc is disarmed, so it reaches the focused app again. Cancelling then
    /// would cut a paste short and put the previous clipboard back before the app had read it.
    /// </para>
    /// </remarks>
    public async Task CancelAsync(bool includeTranscription)
    {
        if (includeTranscription)
        {
            // The flag rather than State: it is cleared under this lock, so an Esc that loses
            // the race to the end of a dictation cannot leave a cancel behind for the next one.
            lock (_processingLock)
            {
                if (_cancellable)
                {
                    if (_processing is null) _cancelPending = true;
                    else _processing.Cancel();
                    return;
                }
            }
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DictationState.Recording) return;

            // Taken from the field once: DisposeAsync does the same without the gate.
            var recording = Interlocked.Exchange(ref _recording, null);
            await CancelQuietlyAsync(recording).ConfigureAwait(false);
            lock (_bufferLock)
            {
                _buffer?.Dispose();
                _buffer = null;
                Level = 0;
            }

            recording?.Dispose();
            SetState(DictationState.Idle);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnPressed(object? sender, EventArgs e) => _ = BeginAsync();

    private void OnReleased(object? sender, EventArgs e) => _ = EndAsync();

    private void OnCancelPressed(object? sender, EventArgs e) => _ = CancelAsync(includeTranscription: true);

    private async Task BeginAsync()
    {
        CancellationToken recording;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DictationState.Idle) return;

            lock (_bufferLock) _buffer = new SampleBuffer();
            _startedAt = _clock.Now;
            _recording = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

            // Taken under the gate: CancelAsync may clear the field the moment it is released.
            recording = _recording.Token;
            SetState(DictationState.Recording);
        }
        finally
        {
            _gate.Release();
        }

        // This recording's buffer. A capture loop that outlives its recording (cancellation
        // is cooperative) must not append to the next one's.
        SampleBuffer? mine;
        lock (_bufferLock) mine = _buffer;

        try
        {
            await foreach (var chunk in _capture.CaptureAsync(recording).ConfigureAwait(false))
            {
                // Stop consuming the moment recording ends. Cancellation is cooperative, so
                // chunks already queued still arrive after EndAsync has moved on — and
                // without this guard one of them sets Level back to a reading that has
                // already been zeroed.
                if (State != DictationState.Recording) break;

                lock (_bufferLock)
                {
                    // The same guard, made airtight: key release detaches the buffer and zeroes
                    // the level under this lock, so a chunk either lands before that or not at
                    // all — it can never resurrect a level the release has already cleared.
                    if (_buffer is null || !ReferenceEquals(_buffer, mine)) break;

                    // Copied, not referenced: capture implementations are entitled to reuse
                    // their buffer the moment this returns.
                    _buffer.Append(chunk.Samples.Span);
                    Level = chunk.Rms();
                }

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
        SampleBuffer? samples;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DictationState.Recording) return;

            // Left in the field (disposed in the finally below), but read once: DisposeAsync may
            // take it at any moment without the gate.
            await CancelQuietlyAsync(Volatile.Read(ref _recording)).ConfigureAwait(false);
            lock (_bufferLock)
            {
                samples = _buffer;
                _buffer = null;
                Level = 0;
            }

            lock (_processingLock)
            {
                _cancellable = true;
                _cancelPending = false;
            }

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
            samples?.Dispose();

            // Taken from the field once, so DisposeAsync, racing this at quit, disposes it or
            // this does — never both, and neither reads a field the other has just cleared.
            Interlocked.Exchange(ref _recording, null)?.Dispose();
            lock (_processingLock)
            {
                _cancellable = false;
                _cancelPending = false;
            }

            SetState(DictationState.Idle);
        }
    }

    private async Task ProcessAsync(SampleBuffer? samples)
    {
        if (samples is null || samples.Count == 0) return;

        // Measured from key release, because that is the wait the user actually feels — and
        // it is the only figure on which a streaming and a batch engine compare honestly.
        var releasedAt = _clock.Now;

        // The one copy: an exact-length array, so a recogniser that needs a whole float[]
        // (sherpa-onnx does) can take it as is instead of copying again.
        var audio = new ReadOnlyMemory<float>(samples.ToArray());
        var duration = TimeSpan.FromSeconds((double)audio.Length / AudioChunk.SampleRate);

        // One per utterance, linked to the engine's lifetime: shutting down abandons this
        // dictation wherever it has got to, and nothing is typed after the app has gone. Esc
        // cancels it too, through the field.
        using var utterance = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_processingLock)
        {
            _processing = utterance;
            if (_cancelPending) utterance.Cancel();
            _cancelPending = false;
        }

        try
        {
            await ProcessUtteranceAsync(audio, duration, releasedAt, utterance).ConfigureAwait(false);
        }
        finally
        {
            lock (_processingLock)
            {
                _processing = null;
                _cancelPending = false;
            }
        }
    }

    private async Task ProcessUtteranceAsync(
        ReadOnlyMemory<float> audio,
        TimeSpan duration,
        DateTimeOffset releasedAt,
        CancellationTokenSource utterance)
    {
        var entries = _dictionary();
        var bias = DictionaryCorrector.BiasPhrases(entries);

        if (await TranscribeAsync(audio, duration, bias, utterance.Token).ConfigureAwait(false)
            is not { } heard)
        {
            return;
        }

        var raw = heard.Text;
        if (string.IsNullOrWhiteSpace(raw) || utterance.IsCancellationRequested) return;

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
        var inject = _injectText();

        if (string.IsNullOrWhiteSpace(formatted))
        {
            if (processed.Submit && inject) await SubmitAsync(utterance.Token).ConfigureAwait(false);
            return;
        }

        var result = new DictationResult(
            At: releasedAt,
            AudioDuration: duration,
            ProcessingTime: _clock.Now - releasedAt,
            Text: formatted,
            Corrections: processed.Corrections)
        {
            Audio = audio,
            Engine = heard.Engine,
            FellBack = heard.FellBack,
        };

        // From here the dictation is delivered, not abandoned: an Esc that lost the race is too
        // late, and Esc goes back to the focused app, where the user may want it as the text lands.
        lock (_processingLock)
        {
            if (utterance.IsCancellationRequested) return;
            _cancellable = false;
        }

        if (_cancelKey is not null) _cancelKey.CancelArmed = false;

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

        // "Type transcripts into the focused app" switched off: the dictation is kept in
        // history (Completed has already run) and nothing at all is sent to the keyboard.
        if (!inject) return;

        try
        {
            await WithinAsync(
                _limits.InjectionTimeout,
                // A saved prompt goes verbatim: pasted, line breaks and all, however short.
                token => _injector.InjectAsync(formatted, processed.Verbatim, token),
                utterance.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (utterance.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException ex)
        {
            // Enter is not pressed after text that may not have landed: submitting half a
            // message is worse than leaving it for the user to finish.
            LogPipelineError("Typing the transcript timed out", ex);
            return;
        }

        // Enter goes last, after the text has landed. Ordering matters here in a way that is
        // easy to get wrong: submitting first would send an empty message.
        if (processed.Submit) await SubmitAsync(utterance.Token).ConfigureAwait(false);
    }

    /// <summary>What the recogniser heard, before any text pass.</summary>
    private sealed record Heard(string Text, TranscriberKind Engine, bool FellBack);

    /// <summary>Transcribes every segment within the time budget.</summary>
    /// <returns>What was heard, or null when transcription failed or the engine is shutting down.</returns>
    /// <remarks>
    /// Each call runs off this thread and is awaited with the token, so a transcriber that
    /// ignores cancellation — or, like Parakeet's native decode, cannot observe it at all —
    /// still cannot hold the engine past its budget. Abandoned work finishes in the background;
    /// <see cref="LazyTranscriber"/>'s gate keeps the model alive underneath it until it does.
    /// </remarks>
    private async Task<Heard?> TranscribeAsync(
        ReadOnlyMemory<float> audio,
        TimeSpan duration,
        IReadOnlyList<string> bias,
        CancellationToken utterance)
    {
        var allowed = _limits.TranscriptionBudget(duration);
        using var budget = new CancellationTokenSource(allowed, _limits.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(utterance, budget.Token);
        var token = linked.Token;

        var pieces = AudioSegmenter.Split(audio);
        var transcripts = new List<string>(pieces.Count);
        var engine = TranscriberKind.Unknown;
        var fellBack = false;

        try
        {
            foreach (var piece in pieces)
            {
                var text = await Task
                    .Run(() => _transcriber.TranscribeAsync(piece, bias, token).AsTask(), token)
                    .WaitAsync(token)
                    .ConfigureAwait(false);

                if (_transcriber is ITranscriberDescriptor descriptor)
                {
                    engine = descriptor.LastUsedKind;
                    fellBack |= descriptor.LastFellBack;
                }

                if (!string.IsNullOrWhiteSpace(text)) transcripts.Add(text.Trim());
            }
        }
        catch (OperationCanceledException) when (utterance.IsCancellationRequested)
        {
            // Shutting down, or the user pressed Esc. Either way nobody wants to hear about it.
            return null;
        }
        catch (OperationCanceledException ex) when (budget.IsCancellationRequested)
        {
            ReportTranscriptionFailure(new TranscriptionException(
                TranscriptionFailure.Timeout,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Transcribing {duration.TotalSeconds:0.#} s of audio took longer than {allowed.TotalSeconds:0.#} s."),
                innerException: ex));
            return null;
        }
        catch (Exception ex)
        {
            ReportTranscriptionFailure(ex);
            return null;
        }

        return new Heard(string.Join(' ', transcripts), engine, fellBack);
    }

    /// <summary>Logs a failed transcription and tells subscribers.</summary>
    /// <remarks>
    /// A <see cref="TranscriptionException"/> is an expected, explainable failure — no model, a
    /// bad key, a timeout — and goes to its own log. Anything else is a bug and goes to the
    /// crash log with the rest of them.
    /// </remarks>
    private void ReportTranscriptionFailure(Exception ex)
    {
        if (ex is TranscriptionException) AppendLog("last-transcription-error.log", ex.ToString());
        else LogPipelineError("Transcription failed", ex);

        try
        {
            TranscriptionFaulted?.Invoke(this, ex);
        }
        catch (Exception subscriber)
        {
            LogPipelineError("TranscriptionFaulted subscriber failed", subscriber);
        }
    }

    /// <summary>
    /// Runs one keyboard action, cancelled after <paramref name="limit"/> or with the utterance.
    /// </summary>
    /// <remarks>
    /// Called on this thread, unlike transcription: <c>SendInput</c> returns in microseconds
    /// or not at all for reasons a thread hop would not fix, and keystrokes must not be
    /// reordered by a scheduler. The wait is what is bounded, so an injector that goes
    /// asynchronous and never comes back cannot hold the engine.
    /// </remarks>
    private async Task<bool> WithinAsync(
        TimeSpan limit,
        Func<CancellationToken, ValueTask<bool>> action,
        CancellationToken utterance)
    {
        using var timeout = new CancellationTokenSource(limit, _limits.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(utterance, timeout.Token);
        var token = linked.Token;

        return await action(token).AsTask().WaitAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Presses Enter in the focused application.
    /// </summary>
    /// <remarks>
    /// Isolated, because the keystroke is a nice-to-have on top of a dictation that has already
    /// succeeded. An injector that cannot deliver a keypress must not turn a completed
    /// dictation into a logged failure.
    /// </remarks>
    private async Task SubmitAsync(CancellationToken utterance)
    {
        try
        {
            await WithinAsync(_limits.InjectionTimeout, _injector.PressEnterAsync, utterance)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (utterance.IsCancellationRequested)
        {
            // Shutting down.
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

    /// <summary>Serialises log writes within the process.</summary>
    /// <remarks>
    /// Two dictations failing at once — a timed-out one finishing while the next fails — would
    /// otherwise collide on the file, and the loser's write is swallowed below, losing exactly
    /// the record the log exists for.
    /// </remarks>
    private static readonly Lock LogLock = new();

    private static void AppendLog(string fileName, string message)
    {
        try
        {
            using var scope = LogLock.EnterScope();
            var directory = MurmurPaths.DataDirectory;
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
        var moved = State != state;
        State = state;

        // Esc is consumed only while there is a dictation to cancel, and never at rest.
        if (_cancelKey is not null) _cancelKey.CancelArmed = state != DictationState.Idle;

        // At rest the gesture starts over: a hands-free latch cannot outlive its recording.
        if (moved && state == DictationState.Idle) _gesture?.Reset();

        Changed?.Invoke(this, EventArgs.Empty);
        if (moved) StateChanged?.Invoke(this, state);
    }

    /// <summary>Cancels <paramref name="source"/>, tolerating one another thread has just disposed.</summary>
    private static async Task CancelQuietlyAsync(CancellationTokenSource? source)
    {
        if (source is null) return;

        try
        {
            await source.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed by the other side of a race at quit; it was cancelled before that.
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Cancels whatever is in flight first. A dictation still being transcribed is abandoned
    /// rather than waited for: its text would be typed into whatever has focus after the app
    /// is gone, which is never what quitting meant.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        _hotkey.Pressed -= OnPressed;
        _hotkey.Released -= OnReleased;
        if (_cancelKey is not null)
        {
            _cancelKey.CancelPressed -= OnCancelPressed;
            _cancelKey.CancelArmed = false;
        }

        _hotkey.Dispose();

        // Without the gate, so taken from the field exactly once: a transcription unwinding on
        // another thread clears the same field in EndAsync's finally.
        if (Interlocked.Exchange(ref _recording, null) is { } recording)
        {
            await CancelQuietlyAsync(recording).ConfigureAwait(false);
            recording.Dispose();
        }

        lock (_bufferLock)
        {
            _buffer?.Dispose();
            _buffer = null;
        }

        await _capture.DisposeAsync().ConfigureAwait(false);
        await _transcriber.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
