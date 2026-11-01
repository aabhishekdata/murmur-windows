using Murmur.Abstractions;

namespace Murmur.Core;

/// <summary>
/// Hold-to-talk, plus a double tap that latches hands-free, between the raw key and the engine.
/// </summary>
/// <remarks>
/// <para>
/// Sits where the hotkey source used to plug straight into <see cref="DictationEngine"/>, and is
/// one itself, so the engine still sees nothing but "pressed" and "released":
/// </para>
/// <list type="bullet">
/// <item><b>Hold</b> works exactly as before. The press starts recording at once and a release
/// after <see cref="TapLength"/> or more stops it.</item>
/// <item><b>A tap</b> — released within <see cref="TapLength"/> — keeps recording for up to
/// <see cref="DoubleTapWindow"/>, waiting for a second press. If none comes, the tap is
/// <see cref="Released"/> as the short hold it was, and transcribed: a quick "yes" is still a
/// dictation. It only arrives a window later.</item>
/// <item><b>A second press inside the window latches</b> hands-free. Both keys can be let go and
/// recording carries on, the audio from the first tap included; the next press stops it.</item>
/// </list>
/// <para>
/// With <c>enabled</c> answering false a tap is an ordinary short hold, as it always was.
/// </para>
/// <para>
/// One timer, created at construction and armed only between a tap and the second press. At rest,
/// and through an ordinary hold, nothing is scheduled at all.
/// </para>
/// <para>
/// The raw key's events arrive on the hook thread, the timer on the thread pool and
/// <see cref="Reset()"/> on the engine's; a lock orders them, and every event is raised outside it.
/// </para>
/// </remarks>
public sealed class PushToTalkGesture : IHotkeySource, ICancelKeySource
{
    /// <summary>The longest press that still counts as a tap.</summary>
    public static readonly TimeSpan TapLength = TimeSpan.FromMilliseconds(300);

    /// <summary>How long after a tap's release a second press latches hands-free.</summary>
    public static readonly TimeSpan DoubleTapWindow = TimeSpan.FromMilliseconds(300);

    private readonly IHotkeySource _inner;
    private readonly ICancelKeySource? _cancel;
    private readonly TimeProvider _time;
    private readonly Func<bool> _enabled;
    private readonly ITimer _timer;
    private readonly Lock _lock = new();

    private Phase _phase = Phase.Idle;
    private DateTimeOffset _pressedAt;
    private bool _windowOpen;

    /// <summary>Where the gesture has got to.</summary>
    private enum Phase
    {
        /// <summary>Nothing held, nothing recording because of the key.</summary>
        Idle,

        /// <summary>The key is down and recording has started.</summary>
        Holding,

        /// <summary>A tap was released; recording continues while a second press is awaited.</summary>
        TapPending,

        /// <summary>The second press of a double tap is still down. Hands-free from here.</summary>
        LatchingHeld,

        /// <summary>Hands-free: recording with no key held.</summary>
        Latched,

        /// <summary>The press that stopped hands-free is still down; its release means nothing.</summary>
        StoppingHeld,
    }

    /// <summary>Wraps the raw key.</summary>
    /// <param name="inner">The push-to-talk key itself.</param>
    /// <param name="enabled">Whether a double tap latches, read on every tap.</param>
    /// <param name="time">Drives the double-tap window; replaced in tests.</param>
    public PushToTalkGesture(IHotkeySource inner, Func<bool>? enabled = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _cancel = inner as ICancelKeySource;
        _enabled = enabled ?? (static () => true);
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(
            static state => ((PushToTalkGesture)state!).OnWindowClosed(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _inner.Pressed += OnInnerPressed;
        _inner.Released += OnInnerReleased;
        if (_cancel is not null) _cancel.CancelPressed += OnInnerCancel;
    }

    /// <summary>Start recording.</summary>
    public event EventHandler? Pressed;

    /// <summary>Stop recording and transcribe.</summary>
    public event EventHandler? Released;

    /// <summary>Hands-free started (true) or ended (false). Raised on whichever thread moved it.</summary>
    public event EventHandler<bool>? HandsFreeChanged;

    /// <inheritdoc />
    public event EventHandler? CancelPressed;

    /// <summary>Whether recording is latched on with no key held.</summary>
    public bool IsHandsFree
    {
        get
        {
            lock (_lock) return _phase is Phase.LatchingHeld or Phase.Latched;
        }
    }

    /// <summary>Whether the double-tap window is open. Exposed for tests.</summary>
    public bool IsAwaitingSecondTap
    {
        get
        {
            lock (_lock) return _phase == Phase.TapPending;
        }
    }

    /// <inheritdoc />
    public int VirtualKey
    {
        get => _inner.VirtualKey;
        set
        {
            if (_inner.VirtualKey == value) return;
            _inner.VirtualKey = value;

            // Everything, a hold included: its release belongs to a key no longer watched.
            Forget(includeHold: true);
        }
    }

    /// <inheritdoc />
    public bool CancelArmed
    {
        get => _cancel?.CancelArmed ?? false;
        set
        {
            if (_cancel is not null) _cancel.CancelArmed = value;
        }
    }

    /// <inheritdoc />
    public bool Start() => _inner.Start();

    /// <inheritdoc />
    public void StopListening() => _inner.StopListening();

    /// <summary>Forgets any tap or latch in progress, without raising anything.</summary>
    /// <remarks>
    /// <para>
    /// The engine calls this whenever it comes to rest — a cancel, a finished dictation, Esc — so a
    /// latch can never outlive the recording it belonged to. A release still to come for the
    /// second press of a latch, or the press that stopped one, is then ignored.
    /// </para>
    /// <para>
    /// A plain hold is left alone. Its press may be a new one whose recording is still queued
    /// behind the engine's gate, and swallowing its release would leave that recording running
    /// with no key held. It only loses the right to become a tap: the release is passed on
    /// whatever its length, and the engine ignores it when it is not recording.
    /// </para>
    /// </remarks>
    public void Reset() => Forget(includeHold: false);

    private void Forget(bool includeHold)
    {
        bool wasHandsFree;
        lock (_lock)
        {
            wasHandsFree = _phase is Phase.LatchingHeld or Phase.Latched;
            if (_phase == Phase.Holding && !includeHold) _pressedAt = DateTimeOffset.MinValue;
            else _phase = Phase.Idle;

            CloseWindow();
        }

        if (wasHandsFree) HandsFreeChanged?.Invoke(this, false);
    }

    private void OnInnerPressed(object? sender, EventArgs e)
    {
        Action? raise = null;

        lock (_lock)
        {
            switch (_phase)
            {
                case Phase.Idle:
                    _phase = Phase.Holding;
                    _pressedAt = _time.GetUtcNow();
                    raise = RaisePressed;
                    break;

                case Phase.TapPending:
                    // The second tap of a double tap. Recording never stopped, so nothing is sent
                    // to the engine; the bar learns from HandsFreeChanged.
                    CloseWindow();
                    _phase = Phase.LatchingHeld;
                    raise = () => HandsFreeChanged?.Invoke(this, true);
                    break;

                case Phase.Latched:
                    _phase = Phase.StoppingHeld;
                    raise = () =>
                    {
                        HandsFreeChanged?.Invoke(this, false);
                        RaiseReleased();
                    };
                    break;
            }
        }

        raise?.Invoke();
    }

    private void OnInnerReleased(object? sender, EventArgs e)
    {
        Action? raise = null;

        lock (_lock)
        {
            switch (_phase)
            {
                case Phase.Holding:
                    // Wall time from the provider rather than a timestamp, so a test's clock drives it.
                    var held = _time.GetUtcNow() - _pressedAt;
                    if (held < TapLength && _enabled())
                    {
                        _phase = Phase.TapPending;
                        _timer.Change(DoubleTapWindow, Timeout.InfiniteTimeSpan);
                        _windowOpen = true;
                    }
                    else
                    {
                        _phase = Phase.Idle;
                        raise = RaiseReleased;
                    }

                    break;

                case Phase.LatchingHeld:
                    _phase = Phase.Latched;
                    break;

                case Phase.StoppingHeld:
                    _phase = Phase.Idle;
                    break;
            }
        }

        raise?.Invoke();
    }

    /// <summary>The double-tap window closed with no second press: the tap was a short hold.</summary>
    private void OnWindowClosed()
    {
        lock (_lock)
        {
            _windowOpen = false;
            if (_phase != Phase.TapPending) return;
            _phase = Phase.Idle;
        }

        RaiseReleased();
    }

    private void OnInnerCancel(object? sender, EventArgs e) => CancelPressed?.Invoke(this, EventArgs.Empty);

    /// <summary>Disarms the timer. Called under the lock.</summary>
    private void CloseWindow()
    {
        if (!_windowOpen) return;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _windowOpen = false;
    }

    private void RaisePressed() => Pressed?.Invoke(this, EventArgs.Empty);

    private void RaiseReleased() => Released?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public void Dispose()
    {
        _inner.Pressed -= OnInnerPressed;
        _inner.Released -= OnInnerReleased;
        if (_cancel is not null) _cancel.CancelPressed -= OnInnerCancel;

        _timer.Dispose();
        _inner.Dispose();
    }
}
