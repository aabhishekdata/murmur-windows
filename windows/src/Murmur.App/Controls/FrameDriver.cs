using Avalonia;
using Avalonia.Controls;
using Murmur.App.Design;

namespace Murmur.App.Controls;

/// <summary>
/// Steps a control's animation from the compositor's frame clock, and only while it is moving.
/// </summary>
/// <remarks>
/// <para>
/// Replaces a <c>DispatcherTimer</c> per control that fired every 16 ms from the moment the
/// control was attached until the process exited — for the level bars, that meant sixty
/// wakeups a second, all day, for a row of bars sitting perfectly still.
/// </para>
/// <para>
/// <see cref="TopLevel.RequestAnimationFrame"/> is one-shot: a callback is requested, it runs
/// once on the next frame, and nothing more happens unless it asks again. So the animation asks
/// again only while <c>wants</c> says there is something to animate, and when it settles the
/// requests simply stop. A property change that could start it moving calls
/// <see cref="Restart"/>.
/// </para>
/// <para>
/// Painting is capped at <see cref="Tokens.Motion.FrameCap"/> (about 30 fps). The physics still step in
/// fixed <see cref="Tokens.Motion.Frame"/> increments — several per painted frame when needed —
/// so the motion looks and settles exactly as it did at 60 fps, at half the redraws.
/// </para>
/// </remarks>
internal sealed class FrameDriver
{
    /// <summary>The most physics steps taken in one go, so a stalled UI does not lurch.</summary>
    private const int MaxStepsPerFrame = 4;

    private readonly Visual _owner;
    private readonly Func<bool> _wants;
    private readonly Action<int> _step;

    private int _generation;
    private bool _requested;
    private TimeSpan? _last;
    private TimeSpan _carry;

    /// <summary>Creates a driver for <paramref name="owner"/>.</summary>
    /// <param name="owner">The control being animated; frames are requested from its top level.</param>
    /// <param name="wants">Whether there is anything to animate right now.</param>
    /// <param name="step">Advances the physics by the given number of frames and repaints.</param>
    public FrameDriver(Visual owner, Func<bool> wants, Action<int> step)
    {
        _owner = owner;
        _wants = wants;
        _step = step;
    }

    /// <summary>Whether a frame has been requested and not yet delivered.</summary>
    public bool IsRequested => _requested;

    /// <summary>
    /// Starts requesting frames if there is anything to animate, abandoning any request that
    /// is already outstanding.
    /// </summary>
    /// <remarks>
    /// Abandoning rather than reusing: a request made against a top level the control has
    /// since left may never be answered, and a driver waiting on it would never move again.
    /// The generation stamp makes a late answer to an abandoned request a no-op, so there is
    /// never more than one live chain of frames.
    /// </remarks>
    public void Restart()
    {
        _generation++;
        _requested = false;
        _last = null;
        _carry = TimeSpan.Zero;
        Request();
    }

    /// <summary>
    /// Delivers one frame. Called by the compositor; also called directly by tests.
    /// </summary>
    /// <param name="now">The frame's timestamp.</param>
    public void OnFrame(TimeSpan now) => OnFrame(_generation, now);

    private void Request()
    {
        if (_requested || !_wants()) return;

        var top = TopLevel.GetTopLevel(_owner);
        if (top is null) return;

        _requested = true;
        var generation = _generation;
        top.RequestAnimationFrame(now => OnFrame(generation, now));
    }

    private void OnFrame(int generation, TimeSpan now)
    {
        if (generation != _generation) return;
        _requested = false;

        if (!_wants())
        {
            _last = null;
            _carry = TimeSpan.Zero;
            return;
        }

        // The first frame of a run steps at once, so motion begins without a visible pause.
        if (_last is not { } last)
        {
            _last = now;
            _step(1);
            Request();
            return;
        }

        var elapsed = now - last;
        _last = now;
        if (elapsed > TimeSpan.Zero) _carry += elapsed;

        if (_carry >= Tokens.Motion.FrameCap)
        {
            var steps = (int)Math.Round(_carry / Tokens.Motion.Frame);
            _carry = TimeSpan.Zero;
            _step(Math.Clamp(steps, 1, MaxStepsPerFrame));
        }

        Request();
    }
}
