using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Murmur.App.Design;

namespace Murmur.App.Controls;

/// <summary>What the floating bar is doing. Mirrors the engine, one step removed.</summary>
/// <remarks>
/// A separate enum from <c>DictationState</c> on purpose: the bar has a fourth state the engine
/// does not — "the model isn't installed, nothing will happen if you click me".
/// </remarks>
public enum BarState
{
    /// <summary>Waiting. A small capsule with a dim dot.</summary>
    Idle,

    /// <summary>The key is held. The red dot, and the level bars move.</summary>
    Listening,

    /// <summary>The key is released and the utterance is being transcribed. A word, no spinner.</summary>
    Processing,

    /// <summary>No speech model on disk, so dictation cannot happen at all.</summary>
    Unavailable,

    /// <summary>
    /// Recording latched on by a double tap. As <see cref="Listening"/>, plus the words
    /// "Hands-free — tap to stop".
    /// </summary>
    HandsFree,
}

/// <summary>
/// The level display: a row of rounded bars that rise and fall with the voice.
/// </summary>
/// <remarks>
/// <para>
/// This is the bar's whole visual signature, and it is what a modern dictation widget shows
/// while you speak. Two things are combined: the smoothed input level sets the overall
/// amplitude, and a travelling sine gives each bar its own phase so the row moves rather than
/// pumping as one block.
/// </para>
/// <para>
/// The physics live in plain fields stepped per frame, deliberately kept out of the property
/// system — a styled property invalidated sixty times a second would push a full layout pass
/// each frame for a value only <see cref="Render"/> ever reads.
/// </para>
/// <para>
/// Frames are requested only while the row is live or still settling back to rest (see
/// <see cref="FrameDriver"/>). Once every bar is back at its idle height the row asks for
/// nothing, so an idle bar on screen costs no wakeups at all.
/// </para>
/// </remarks>
public sealed class WaveBars : Control
{
    /// <summary>Current input level, 0…1.</summary>
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<WaveBars, double>(nameof(Level));

    /// <summary>Whether the row is live. At rest the bars sit low and dim.</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<WaveBars, bool>(nameof(IsActive));

    /// <summary>Bars run top-to-bottom instead of left-to-right, for a side-docked bar.</summary>
    public static readonly StyledProperty<bool> IsVerticalProperty =
        AvaloniaProperty.Register<WaveBars, bool>(nameof(IsVertical));

    /// <inheritdoc cref="LevelProperty"/>
    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <inheritdoc cref="IsActiveProperty"/>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <inheritdoc cref="IsVerticalProperty"/>
    public bool IsVertical
    {
        get => GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

    /// <summary>How close to rest a bar must be before it is snapped there and the row stops.</summary>
    private const double SettleTolerance = 0.001;

    private readonly double[] _bars = new double[Tokens.Wave.BarCount];
    private readonly FrameDriver _frames;
    private double _phase;

    static WaveBars() => AffectsRender<WaveBars>(IsActiveProperty, IsVerticalProperty);

    /// <summary>Creates a row at the token dimensions.</summary>
    public WaveBars()
    {
        Array.Fill(_bars, Tokens.Wave.IdleFraction);
        VerticalAlignment = VerticalAlignment.Stretch;
        _frames = new FrameDriver(this, WantsFrames, StepFrames);
    }

    /// <summary>The smoothed bar heights, 0…1. Exposed for headless tests.</summary>
    public IReadOnlyList<double> BarHeights => _bars;

    /// <summary>
    /// Where the level is read from on each animation frame, or null to use <see cref="Level"/>
    /// as set.
    /// </summary>
    /// <remarks>
    /// The floating bar points this at the engine. Reading on the frame clock is the throttle:
    /// the engine updates its level at capture-buffer rate on a background thread, and this
    /// samples it at most once per painted frame — and not at all while the row is at rest.
    /// </remarks>
    public Func<double>? LevelSource { get; set; }

    /// <summary>
    /// Whether the row is asking for animation frames. Exposed for tests.
    /// </summary>
    /// <remarks>Not <c>IsAnimating</c>: that name is already a method on <c>AvaloniaObject</c>.</remarks>
    public bool IsRequestingFrames => _frames.IsRequested;

    /// <summary>Whether every bar is at its resting height.</summary>
    public bool IsSettled => Array.TrueForAll(_bars, static b => b == Tokens.Wave.IdleFraction);

    /// <summary>Delivers one animation frame. Exposed so tests need no compositor.</summary>
    /// <param name="now">The frame's timestamp.</param>
    public void OnFrame(TimeSpan now) => _frames.OnFrame(now);

    private bool WantsFrames() =>
        IsVisible && TopLevel.GetTopLevel(this) is not null && (IsActive || !IsSettled);

    private void StepFrames(int steps)
    {
        if (LevelSource is { } source) Level = source();

        for (var i = 0; i < steps; i++) Advance();
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // A hidden row is at rest by definition. Snapping there, rather than letting it settle
        // unseen, means the next recording starts from flat and nothing asks for frames to
        // animate bars nobody can see.
        if (change.Property == IsVisibleProperty && !IsVisible) Array.Fill(_bars, Tokens.Wave.IdleFraction);

        // The only two things that can set a resting row moving again.
        if (change.Property == IsActiveProperty || change.Property == IsVisibleProperty) _frames.Restart();
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var across = (Tokens.Wave.BarCount * Tokens.Wave.BarWidth)
                   + ((Tokens.Wave.BarCount - 1) * Tokens.Wave.BarGap);

        return IsVertical ? new Size(availableSize.Width, across) : new Size(across, availableSize.Height);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _frames.Restart();
    }

    /// <summary>
    /// Steps every bar one frame toward its target. Exposed so tests can drive the animation
    /// without waiting on a real timer.
    /// </summary>
    public void Advance()
    {
        _phase += Tokens.Wave.TravelHz * Tokens.Motion.Frame.TotalSeconds * Math.Tau;

        var amplitude = IsActive
            ? Math.Clamp(Level * Tokens.Wave.Gain, 0, 1)
            : 0;

        for (var i = 0; i < _bars.Length; i++)
        {
            double target;

            if (IsActive)
            {
                // Each bar is offset along the travelling wave, so the row ripples instead of
                // pumping as a single block.
                var travel = (Math.Sin(_phase - (i * 0.55)) + 1) / 2;
                var shaped = (amplitude * (1 - Tokens.Wave.TravelDepth))
                           + (amplitude * travel * Tokens.Wave.TravelDepth);

                target = Math.Max(Tokens.Wave.IdleFraction, shaped);
            }
            else
            {
                target = Tokens.Wave.IdleFraction;
            }

            var rate = target > _bars[i] ? Tokens.Wave.Smoothing : Tokens.Wave.Decay;
            _bars[i] += (target - _bars[i]) * rate;

            // Exponential decay never quite arrives. Snapping the last invisible fraction is
            // what lets the row declare itself settled and stop asking for frames.
            if (!IsActive && Math.Abs(_bars[i] - target) < SettleTolerance) _bars[i] = target;
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var colour = IsActive ? Tokens.Colors.BarWave : Tokens.Colors.BarWaveIdle;
        var brush = BrushCache.Get(colour);
        var pitch = Tokens.Wave.BarWidth + Tokens.Wave.BarGap;
        var track = IsVertical ? bounds.Width : bounds.Height;

        for (var i = 0; i < _bars.Length; i++)
        {
            var length = Math.Max(Tokens.Wave.MinBarLength, _bars[i] * track);

            var slot = IsVertical
                ? new Rect((bounds.Width - length) / 2, i * pitch, length, Tokens.Wave.BarWidth)
                : new Rect(i * pitch, (bounds.Height - length) / 2, Tokens.Wave.BarWidth, length);

            context.DrawRectangle(brush, null, new RoundedRect(slot, Tokens.Wave.BarWidth / 2));
        }
    }
}

