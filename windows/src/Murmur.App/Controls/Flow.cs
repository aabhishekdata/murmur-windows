using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Murmur.App.Design;

namespace Murmur.App.Controls;

/// <summary>What the floating bar is doing. Mirrors the engine, one step removed.</summary>
/// <remarks>
/// A separate enum from <c>DictationState</c> on purpose: the bar has a fourth state the engine
/// does not — "the model isn't installed, nothing will happen if you click me".
/// </remarks>
public enum BarState
{
    /// <summary>Waiting. Small and quiet.</summary>
    Idle,

    /// <summary>The key is held. The level bars move.</summary>
    Listening,

    /// <summary>The key is released and the utterance is being transcribed.</summary>
    Processing,

    /// <summary>No speech model on disk, so dictation cannot happen at all.</summary>
    Unavailable,
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
/// The physics live in plain fields stepped by a timer, deliberately kept out of the property
/// system — a styled property invalidated sixty times a second would push a full layout pass
/// each frame for a value only <see cref="Render"/> ever reads.
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

    private readonly double[] _bars = new double[Tokens.Wave.BarCount];
    private double _phase;
    private DispatcherTimer? _ticker;

    static WaveBars() => AffectsRender<WaveBars>(IsActiveProperty, IsVerticalProperty);

    /// <summary>Creates a row at the token dimensions.</summary>
    public WaveBars()
    {
        Array.Fill(_bars, Tokens.Wave.IdleFraction);
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>The smoothed bar heights, 0…1. Exposed for headless tests.</summary>
    public IReadOnlyList<double> BarHeights => _bars;

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

        _ticker = new DispatcherTimer(DispatcherPriority.Render) { Interval = Tokens.Motion.Frame };
        _ticker.Tick += (_, _) => { Advance(); InvalidateVisual(); };
        _ticker.Start();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _ticker?.Stop();
        _ticker = null;
        base.OnDetachedFromVisualTree(e);
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
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var colour = IsActive ? Tokens.Colors.BarWave : Tokens.Colors.BarWaveIdle;
        var brush = new SolidColorBrush(colour);
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

/// <summary>
/// A small filled circle used as a state indicator.
/// </summary>
/// <remarks>
/// Flat, with no lens, no bevel and no glow — the previous design's lamp was a physical object
/// pretending to be lit, and this is a dot.
/// </remarks>
public sealed class StatusDot : Control
{
    /// <summary>Whether the dot is showing its live colour.</summary>
    public static readonly StyledProperty<bool> IsLitProperty =
        AvaloniaProperty.Register<StatusDot, bool>(nameof(IsLit));

    /// <summary>The colour when lit.</summary>
    public static readonly StyledProperty<Color> DotColorProperty =
        AvaloniaProperty.Register<StatusDot, Color>(nameof(DotColor), Tokens.Colors.Live);

    /// <summary>Opacity when unlit.</summary>
    public static readonly StyledProperty<double> UnlitOpacityProperty =
        AvaloniaProperty.Register<StatusDot, double>(nameof(UnlitOpacity), 0.3);

    /// <inheritdoc cref="IsLitProperty"/>
    public bool IsLit
    {
        get => GetValue(IsLitProperty);
        set => SetValue(IsLitProperty, value);
    }

    /// <inheritdoc cref="DotColorProperty"/>
    public Color DotColor
    {
        get => GetValue(DotColorProperty);
        set => SetValue(DotColorProperty, value);
    }

    /// <inheritdoc cref="UnlitOpacityProperty"/>
    public double UnlitOpacity
    {
        get => GetValue(UnlitOpacityProperty);
        set => SetValue(UnlitOpacityProperty, value);
    }

    static StatusDot() => AffectsRender<StatusDot>(IsLitProperty, DotColorProperty, UnlitOpacityProperty);

    /// <summary>Creates a dot at the token size.</summary>
    public StatusDot()
    {
        Width = Tokens.Bar.DotSize;
        Height = Tokens.Bar.DotSize;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var brush = new SolidColorBrush(DotColor, IsLit ? 1 : UnlitOpacity);

        context.DrawEllipse(brush, null, centre, size / 2, size / 2);
    }
}

/// <summary>
/// A small, quiet label: section headers, counts, timestamps.
/// </summary>
/// <remarks>
/// Replaces the old silkscreen label, which force-uppercased everything. Sentence case is kept
/// as written — uppercase everywhere is a machine-panel convention, and this is not one.
/// </remarks>
public sealed class Caption : TextBlock
{
    /// <summary>Renders as a section header: slightly larger, tracked, and stronger.</summary>
    public static readonly StyledProperty<bool> IsHeaderProperty =
        AvaloniaProperty.Register<Caption, bool>(nameof(IsHeader));

    /// <inheritdoc cref="IsHeaderProperty"/>
    public bool IsHeader
    {
        get => GetValue(IsHeaderProperty);
        set => SetValue(IsHeaderProperty, value);
    }

    /// <summary>Creates an empty caption.</summary>
    public Caption()
    {
        FontFamily = Tokens.Fonts.Ui;
        FontSize = Tokens.Fonts.Caption;
        FontWeight = FontWeight.Medium;
        Foreground = Tokens.Brushes.InkMuted;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Set here rather than in a render or measure override: assigning a property during a
        // layout or render pass invalidates the visual mid-pass, which Avalonia throws on
        // rather than merely logging.
        if (change.Property == IsHeaderProperty)
        {
            FontSize = IsHeader ? Tokens.Fonts.Label : Tokens.Fonts.Caption;
            FontWeight = IsHeader ? FontWeight.SemiBold : FontWeight.Medium;
            LetterSpacing = IsHeader ? Tokens.Fonts.HeaderTracking : 0;
            Foreground = IsHeader ? Tokens.Brushes.Ink : Tokens.Brushes.InkMuted;
        }
    }
}

/// <summary>How much visual weight a <see cref="SoftButton"/> carries.</summary>
public enum ButtonTone
{
    /// <summary>Text only until hovered. The default for row actions.</summary>
    Quiet,

    /// <summary>A bordered button on the surface.</summary>
    Outline,

    /// <summary>Filled with the accent. One per screen at most.</summary>
    Primary,

    /// <summary>Bordered, in the destructive colour.</summary>
    Danger,
}

/// <summary>
/// The one button in the app.
/// </summary>
/// <remarks>
/// A rounded rectangle that changes fill on hover and press. No travel, no bevel, no inversion
/// — the previous design's transport key sank when held, which is the sort of detail that makes
/// software look like hardware, and this design does not want that.
/// </remarks>
public sealed class SoftButton : Button
{
    /// <summary>How much weight the button carries.</summary>
    public static readonly StyledProperty<ButtonTone> ToneProperty =
        AvaloniaProperty.Register<SoftButton, ButtonTone>(nameof(Tone));

    /// <summary>Whether the button reads as the current selection.</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<SoftButton, bool>(nameof(IsSelected));

    /// <summary>Draw against the bar's dark body rather than a light surface.</summary>
    public static readonly StyledProperty<bool> IsOnBarProperty =
        AvaloniaProperty.Register<SoftButton, bool>(nameof(IsOnBar));

    /// <inheritdoc cref="ToneProperty"/>
    public ButtonTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <inheritdoc cref="IsSelectedProperty"/>
    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <inheritdoc cref="IsOnBarProperty"/>
    public bool IsOnBar
    {
        get => GetValue(IsOnBarProperty);
        set => SetValue(IsOnBarProperty, value);
    }

    static SoftButton() => AffectsRender<SoftButton>(
        ToneProperty, IsSelectedProperty, IsOnBarProperty, IsPressedProperty,
        IsPointerOverProperty, IsEnabledProperty);

    /// <summary>Creates a quiet button.</summary>
    public SoftButton()
    {
        Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug - Tokens.Space.Hair);
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Background = null;
        BorderBrush = null;
        BorderThickness = new Thickness(0);
        FontFamily = Tokens.Fonts.Ui;
        FontSize = Tokens.Fonts.Label;
        FontWeight = FontWeight.Medium;
        Foreground = Tokens.Brushes.Ink;
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var shape = new RoundedRect(bounds, Tokens.Radius.Control);
        var (fill, stroke) = Palette();

        if (fill is not null) context.DrawRectangle(fill, null, shape);
        if (stroke is not null)
        {
            context.DrawRectangle(null, new Pen(stroke, Tokens.Border.Hairline), shape);
        }
    }

    private (IBrush? Fill, IBrush? Stroke) Palette()
    {
        var emphasis = !IsEnabled ? 0 : IsPressed ? 0.16 : IsPointerOver ? 0.09 : 0;

        switch (Tone)
        {
            case ButtonTone.Primary:
                return (new SolidColorBrush(Tokens.Colors.Accent, IsPressed ? 0.85 : 1), null);

            case ButtonTone.Danger:
                return (new SolidColorBrush(Tokens.Colors.Live, emphasis),
                        new SolidColorBrush(Tokens.Colors.Live, 0.45));

            case ButtonTone.Outline:
                return (new SolidColorBrush(OnSurface, emphasis),
                        new SolidColorBrush(IsOnBar ? Tokens.Colors.BarEdge : Tokens.Colors.Line));

            default:
                if (IsSelected)
                {
                    return (new SolidColorBrush(Tokens.Colors.AccentSoft), null);
                }

                return emphasis > 0 ? (new SolidColorBrush(OnSurface, emphasis), null) : (null, null);
        }
    }

    private Color OnSurface => IsOnBar ? Tokens.Colors.BarInk : Tokens.Colors.Ink;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ToneProperty ||
            change.Property == IsSelectedProperty ||
            change.Property == IsOnBarProperty)
        {
            Foreground = new SolidColorBrush(TextColour());
        }
        else if (change.Property == IsEnabledProperty)
        {
            // The button draws itself, so the Fluent theme's disabled visual state never
            // reaches it — without this a dead "Delete all" looks exactly like a live one.
            Opacity = IsEnabled ? 1 : DisabledOpacity;
        }
    }

    /// <summary>How far a disabled button fades.</summary>
    private const double DisabledOpacity = 0.4;

    private Color TextColour() => Tone switch
    {
        ButtonTone.Primary => Tokens.Colors.OnAccent,
        ButtonTone.Danger => Tokens.Colors.Live,
        _ when IsSelected => Tokens.Colors.Accent,
        _ when IsOnBar => Tokens.Colors.BarInk,
        _ => Tokens.Colors.Ink,
    };
}

/// <summary>
/// A sidebar navigation row: a glyph, a label, and a selected state.
/// </summary>
/// <remarks>
/// The glyph is a character from the interface font rather than an icon asset. Shipping icon
/// files for three navigation rows is not worth the download or the scaling problem, and Inter
/// carries every glyph used here.
/// </remarks>
public sealed class NavItem : Button
{
    /// <summary>Whether this row is the current section.</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<NavItem, bool>(nameof(IsSelected));

    /// <inheritdoc cref="IsSelectedProperty"/>
    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    private readonly TextBlock _label;

    static NavItem() => AffectsRender<NavItem>(IsSelectedProperty, IsPointerOverProperty, IsPressedProperty);

    /// <summary>Creates a navigation row.</summary>
    /// <param name="glyph">A single character shown ahead of the label.</param>
    /// <param name="label">The section name.</param>
    public NavItem(string glyph, string label)
    {
        Name = label;
        Padding = new Thickness(Tokens.Space.Base, Tokens.Space.Snug + Tokens.Space.Hair);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        Background = null;
        BorderThickness = new Thickness(0);
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);

        _label = new TextBlock
        {
            Text = label,
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Body,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.Brushes.InkMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Base,
            Children =
            {
                new TextBlock
                {
                    Text = glyph,
                    FontFamily = Tokens.Fonts.Ui,
                    FontSize = Tokens.Fonts.Subtitle,
                    Foreground = Tokens.Brushes.InkFaint,
                    Width = Tokens.Space.Roomy,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                _label,
            },
        };
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var shape = new RoundedRect(bounds, Tokens.Radius.Control);

        if (IsSelected)
        {
            context.DrawRectangle(new SolidColorBrush(Tokens.Colors.AccentSoft), null, shape);
        }
        else if (IsPointerOver)
        {
            context.DrawRectangle(new SolidColorBrush(Tokens.Colors.Hover), null, shape);
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsSelectedProperty)
        {
            _label.Foreground = IsSelected ? Tokens.Brushes.Accent : Tokens.Brushes.InkMuted;
            _label.FontWeight = IsSelected ? FontWeight.SemiBold : FontWeight.Medium;
        }
    }
}

/// <summary>
/// Three dots that pulse in sequence while an utterance is being transcribed.
/// </summary>
/// <remarks>
/// The processing state needs its own vocabulary. Leaving the level bars up implies the app is
/// still listening — which is exactly the moment a user starts talking into a microphone that
/// has already stopped recording.
/// </remarks>
public sealed class PulseDots : Control
{
    /// <summary>How many dots.</summary>
    public const int Count = 3;

    /// <summary>Dot diameter.</summary>
    public const double DotSize = 5;

    /// <summary>Gap between dots.</summary>
    public const double Gap = 5;

    private double _phase;
    private DispatcherTimer? _ticker;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(
        (Count * DotSize) + ((Count - 1) * Gap), Math.Max(DotSize, availableSize.Height));

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _ticker = new DispatcherTimer(DispatcherPriority.Render) { Interval = Tokens.Motion.Frame };
        _ticker.Tick += (_, _) => { Advance(); InvalidateVisual(); };
        _ticker.Start();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _ticker?.Stop();
        _ticker = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Steps the pulse one frame. Exposed so tests need no real timer.</summary>
    public void Advance() =>
        _phase += Tokens.Motion.Frame.TotalSeconds / Tokens.Motion.Pulse.TotalSeconds * Math.Tau;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var centreY = Bounds.Height / 2;

        for (var i = 0; i < Count; i++)
        {
            var wave = (Math.Sin(_phase - (i * 0.7)) + 1) / 2;
            var brush = new SolidColorBrush(Tokens.Colors.BarWave, 0.25 + (wave * 0.75));
            var centre = new Point((i * (DotSize + Gap)) + (DotSize / 2), centreY);

            context.DrawEllipse(brush, null, centre, DotSize / 2, DotSize / 2);
        }
    }
}
