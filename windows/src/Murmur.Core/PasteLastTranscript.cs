using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>
/// Remembers the last transcript and pastes it again from a global shortcut, once the user has
/// chosen one. None is taken by default.
/// </summary>
/// <remarks>
/// <para>
/// For the dictation that landed in the wrong window, or that the user wants a second time. The
/// text goes through the same injector a dictation does, so it arrives the way the original
/// did, with the clipboard put back afterwards.
/// </para>
/// <para>
/// The last transcript is held in memory whether or not history is kept, and seeded from the
/// newest history record at start-up when it is. The tray and the bar <i>copy</i> it rather than
/// paste it: clicking either takes focus away from the window the text was meant for.
/// </para>
/// <para>
/// Registered under one fixed id, clear of the 1-up ids saved prompts use and of their probe. A
/// prompt already holding the same combination keeps it, and this reports
/// <see cref="SnippetHotkeyStatus.Duplicate"/> rather than blaming another app.
/// </para>
/// </remarks>
public sealed class PasteLastTranscript : IShortcutOwner, IDisposable
{
    /// <summary>The registration id. Prompts count up from 1; their probe is <c>0xBFFF</c>.</summary>
    public const int HotkeyId = 0xBFFE;

    private readonly IGlobalHotkeys? _hotkeys;
    private readonly ITextInjector _injector;
    private readonly Func<Task>? _cancelDictation;
    private readonly TimeSpan _injectionTimeout;
    private readonly Func<HotkeyChord, bool> _heldByPrompt;
    private readonly Lock _bindSync = new();

    /// <summary>Guards <see cref="_pending"/> only; never held across a platform call.</summary>
    private readonly Lock _pasteSync = new();
    private Task<bool> _pending = Task.FromResult(false);

    private string? _text;
    private bool _registered;
    private int _suspended;
    private bool _disposed;

    /// <summary>Creates the service.</summary>
    /// <param name="hotkeys">The registration API, or null where there is none.</param>
    /// <param name="injector">Where the text goes.</param>
    /// <param name="cancelDictation">Abandons a recording in progress; null when there is no engine.</param>
    /// <param name="injectionTimeout">How long one paste may take. Ten seconds, as dictation.</param>
    /// <param name="heldByPrompt">
    /// Whether an enabled saved prompt has a combination, so a clash with one is reported as
    /// Murmur's own rather than as another app's. Null where there are no prompts.
    /// </param>
    public PasteLastTranscript(
        IGlobalHotkeys? hotkeys,
        ITextInjector injector,
        Func<Task>? cancelDictation = null,
        TimeSpan? injectionTimeout = null,
        Func<HotkeyChord, bool>? heldByPrompt = null)
    {
        ArgumentNullException.ThrowIfNull(injector);

        _hotkeys = hotkeys;
        _injector = injector;
        _cancelDictation = cancelDictation;
        _injectionTimeout = injectionTimeout ?? TimeSpan.FromSeconds(10);
        _heldByPrompt = heldByPrompt ?? (static _ => false);

        if (_hotkeys is not null) _hotkeys.Pressed += OnPressed;
    }

    /// <summary>The last transcript, or null before the first.</summary>
    public string? Text => Volatile.Read(ref _text);

    /// <summary>The combination bound, or null for none.</summary>
    public HotkeyChord? Chord { get; private set; }

    /// <summary>Whether the combination is working.</summary>
    public SnippetHotkeyStatus Status { get; private set; } = SnippetHotkeyStatus.None;

    /// <summary>Raised when <see cref="Text"/> changes. May fire on any thread.</summary>
    public event EventHandler? TextChanged;

    /// <summary>Raised when pasting failed. Fires on a background thread.</summary>
    public event EventHandler<Exception>? PasteFaulted;

    /// <summary>Makes <paramref name="text"/> the one the shortcut pastes.</summary>
    /// <param name="text">A finished transcript. Blank text is ignored.</param>
    public void Remember(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Volatile.Write(ref _text, text);
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Registers <paramref name="chord"/>, giving back whatever was registered before.</summary>
    /// <param name="chord">The combination, or null for none.</param>
    /// <returns>Where it stands.</returns>
    public SnippetHotkeyStatus Bind(HotkeyChord? chord)
    {
        lock (_bindSync)
        {
            Chord = chord;
            return ApplyLocked();
        }
    }

    /// <summary>Makes the registration match <see cref="Chord"/> and the suspension. Called under the lock.</summary>
    private SnippetHotkeyStatus ApplyLocked()
    {
        if (_disposed) return SnippetHotkeyStatus.None;

        if (_registered) _hotkeys?.Unregister(HotkeyId);
        _registered = false;

        if (Chord is not { } c)
        {
            Status = SnippetHotkeyStatus.None;
        }
        else if (HotkeyChords.Problem(c) is not null)
        {
            Status = SnippetHotkeyStatus.Invalid;
        }
        else if (_hotkeys is null)
        {
            Status = SnippetHotkeyStatus.Unsupported;
        }
        else if (_suspended > 0)
        {
            // Reported as it will be once resumed; the recorder is open, not the problem.
            Status = SnippetHotkeyStatus.Active;
        }
        else
        {
            _registered = _hotkeys.Register(HotkeyId, c);
            Status = _registered ? SnippetHotkeyStatus.Active
                : _heldByPrompt(c) ? SnippetHotkeyStatus.Duplicate
                : SnippetHotkeyStatus.InUse;
        }

        return Status;
    }

    /// <summary>Gives the combination back until the returned handle is disposed.</summary>
    /// <returns>Disposing it registers the combination again.</returns>
    /// <remarks>
    /// While the user is recording a new shortcut: a registered one is swallowed by Windows before
    /// the recorder could see it, and would paste into Settings instead.
    /// </remarks>
    public IDisposable Suspend()
    {
        lock (_bindSync)
        {
            _suspended++;
            ApplyLocked();
        }

        return new Resumer(this);
    }

    /// <summary>Pastes the last transcript into the focused application.</summary>
    /// <returns>Whether anything was delivered; false with nothing to paste.</returns>
    public Task<bool> PasteAsync() => Text is { } text ? PasteAsync(text) : Task.FromResult(false);

    /// <summary>Pastes <paramref name="text"/> into the focused application.</summary>
    /// <param name="text">What to paste — History's Paste button passes the row's text.</param>
    /// <returns>Whether it was delivered. Failures are reported through <see cref="PasteFaulted"/>.</returns>
    /// <remarks>One at a time: a second press is chained behind the first paste.</remarks>
    public Task<bool> PasteAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return Task.FromResult(false);

        lock (_pasteSync)
        {
            _pending = _pending.ContinueWith(
                _ => RunPasteAsync(text),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
            return _pending;
        }
    }

    private async Task<bool> RunPasteAsync(string text)
    {
        try
        {
            // Pressed mid-recording, the user wants this rather than what they were saying.
            if (_cancelDictation is not null) await _cancelDictation().ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(_injectionTimeout);
            return await _injector.InjectAsync(text, timeout.Token)
                .AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fired from the hotkey thread with nobody awaiting it; reported rather than lost.
            PasteFaulted?.Invoke(this, ex);
            return false;
        }
    }

    private void OnPressed(object? sender, int id)
    {
        // Returns at once: this runs on the registration thread, which must keep pumping.
        if (id == HotkeyId) _ = PasteAsync();
    }

    private sealed class Resumer(PasteLastTranscript owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // Whatever was bound while suspended — a newly recorded shortcut, or none — is what
            // gets registered now.
            lock (owner._bindSync)
            {
                owner._suspended--;
                owner.ApplyLocked();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_hotkeys is not null) _hotkeys.Pressed -= OnPressed;

        lock (_bindSync)
        {
            if (_disposed) return;
            _disposed = true;

            if (_registered) _hotkeys?.Unregister(HotkeyId);
            _registered = false;
        }
    }
}
