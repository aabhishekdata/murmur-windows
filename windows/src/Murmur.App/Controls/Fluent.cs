using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Murmur.App.Design;

namespace Murmur.App.Controls;

/// <summary>
/// Repaints a self-drawing control when the theme or the accent changes.
/// </summary>
/// <remarks>
/// A control that draws itself reads <see cref="Tokens.Colors"/> in <c>Render</c>, so it is right
/// on every paint — but nothing asks it to paint when only the palette changed. A theme switch
/// changes the control's own <c>ActualThemeVariant</c>; an accent change replaces application
/// resources, which raises <c>ResourcesChanged</c> down the tree. Both are events, not polling.
/// </remarks>
internal static class ThemeAware
{
    /// <summary>Calls <paramref name="refresh"/> (on the UI thread) whenever the palette changes.</summary>
    /// <param name="control">The control to watch.</param>
    /// <param name="refresh">What to do — usually <c>InvalidateVisual</c>.</param>
    public static void Watch(StyledElement control, Action refresh)
    {
        control.ActualThemeVariantChanged += (_, _) => refresh();
        control.ResourcesChanged += (_, _) => refresh();
    }
}

/// <summary>
/// A small filled circle used as a state indicator.
/// </summary>
/// <remarks>
/// Flat, with no lens, no bevel and no glow. Give it a <see cref="Token"/> and it follows the
/// theme; give it a <see cref="DotColor"/> and it is that colour in every theme — which is what
/// the floating bar, dark everywhere, wants.
/// </remarks>
public sealed class StatusDot : Control
{
    /// <summary>Whether the dot is showing its live colour.</summary>
    public static readonly StyledProperty<bool> IsLitProperty =
        AvaloniaProperty.Register<StatusDot, bool>(nameof(IsLit));

    /// <summary>The colour when lit, used when no <see cref="Token"/> is set.</summary>
    public static readonly StyledProperty<Color> DotColorProperty =
        AvaloniaProperty.Register<StatusDot, Color>(nameof(DotColor), Tokens.Colors.BarLive);

    /// <summary>A themed colour, which wins over <see cref="DotColor"/>.</summary>
    public static readonly StyledProperty<ColorToken?> TokenProperty =
        AvaloniaProperty.Register<StatusDot, ColorToken?>(nameof(Token));

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

    /// <inheritdoc cref="TokenProperty"/>
    public ColorToken? Token
    {
        get => GetValue(TokenProperty);
        set => SetValue(TokenProperty, value);
    }

    /// <inheritdoc cref="UnlitOpacityProperty"/>
    public double UnlitOpacity
    {
        get => GetValue(UnlitOpacityProperty);
        set => SetValue(UnlitOpacityProperty, value);
    }

    static StatusDot() => AffectsRender<StatusDot>(IsLitProperty, DotColorProperty, TokenProperty, UnlitOpacityProperty);

    /// <summary>Creates a dot at the token size.</summary>
    public StatusDot()
    {
        Width = Tokens.Bar.DotSize;
        Height = Tokens.Bar.DotSize;
        ThemeAware.Watch(this, InvalidateVisual);
    }

    /// <summary>The colour the dot draws with now.</summary>
    public Color EffectiveColor => Token is { } token
        ? ThemeResources.Resolve(token, ThemeResources.KindOf(ActualThemeVariant))
        : DotColor;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var brush = BrushCache.Get(EffectiveColor, IsLit ? 1 : UnlitOpacity);

        context.DrawEllipse(brush, null, centre, size / 2, size / 2);
    }
}

/// <summary>
/// A small, quiet label: counts, timestamps, and the header above a group of settings.
/// </summary>
/// <remarks>
/// Sentence case as written. A header is Windows' "BodyStrong" — body size, semibold, primary
/// text — which is how Settings labels a group of cards.
/// </remarks>
public sealed class Caption : TextBlock
{
    /// <summary>Renders as a section header: larger, stronger, primary colour.</summary>
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
        FontWeight = FontWeight.Normal;
        Bind(ForegroundProperty, Tokens.Live.InkMuted);
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
            FontSize = IsHeader ? Tokens.Fonts.Body : Tokens.Fonts.Caption;
            FontWeight = IsHeader ? FontWeight.SemiBold : FontWeight.Normal;
            LetterSpacing = IsHeader ? Tokens.Fonts.HeaderTracking : 0;
            Bind(ForegroundProperty, IsHeader ? Tokens.Live.Ink : Tokens.Live.InkMuted);
        }
    }
}

/// <summary>How much visual weight a <see cref="SoftButton"/> carries.</summary>
public enum ButtonTone
{
    /// <summary>No fill until hovered — Windows' subtle button. The default for row actions.</summary>
    Quiet,

    /// <summary>The standard Windows 11 button.</summary>
    Outline,

    /// <summary>Filled with the accent. One per screen at most.</summary>
    Primary,

    /// <summary>
    /// A destructive action. Drawn as a standard button — red is reserved for recording — and
    /// kept as a tone so the intent stays visible in code.
    /// </summary>
    Danger,
}

/// <summary>
/// The app's button: a Windows 11 Fluent button with a tone.
/// </summary>
/// <remarks>
/// <para>
/// Drawn by the Fluent theme, not by hand: the elevation border, the hover and press fills, the
/// disabled state and the focus ring are all Windows' own, and all follow the theme live. The
/// tone only chooses between the theme's variants — the accent style for
/// <see cref="ButtonTone.Primary"/>, a transparent face for <see cref="ButtonTone.Quiet"/>.
/// </para>
/// <para>
/// An optional <see cref="Icon"/> sits ahead of the label, as on Windows' own command buttons.
/// </para>
/// </remarks>
public sealed class SoftButton : Button
{
    /// <summary>How much weight the button carries.</summary>
    public static readonly StyledProperty<ButtonTone> ToneProperty =
        AvaloniaProperty.Register<SoftButton, ButtonTone>(nameof(Tone), ButtonTone.Outline);

    /// <summary>An icon from <see cref="Icons"/> shown ahead of the label, or null.</summary>
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<SoftButton, string?>(nameof(Icon));

    /// <inheritdoc cref="ToneProperty"/>
    public ButtonTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <inheritdoc cref="IconProperty"/>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The label, whatever the icon does to <see cref="ContentControl.Content"/>.</summary>
    public string Label => _label ?? Content as string ?? string.Empty;

    private string? _label;

    /// <inheritdoc />
    /// <remarks>
    /// Drawn with the Fluent <see cref="Button"/> template. Without this a subclass has no theme
    /// of its own to find.
    /// </remarks>
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Creates a standard button.</summary>
    public SoftButton()
    {
        FontFamily = Tokens.Fonts.Ui;
        FontSize = Tokens.Fonts.Body;
        VerticalAlignment = VerticalAlignment.Center;
        CornerRadius = new CornerRadius(Tokens.Radius.Control);
        ApplyTone();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ToneProperty) ApplyTone();
        else if (change.Property == IconProperty || (change.Property == ContentProperty && change.NewValue is string))
        {
            ApplyIcon();
        }
    }

    private void ApplyTone()
    {
        Classes.Set("accent", Tone == ButtonTone.Primary);

        if (Tone == ButtonTone.Quiet)
        {
            Background = Brushes.Transparent;
            BorderBrush = Brushes.Transparent;
        }
        else
        {
            ClearValue(BackgroundProperty);
            ClearValue(BorderBrushProperty);
        }

        // Danger is a standard button on purpose, not a red one: red means recording, and
        // nothing else in the app is red. What makes it safe is its label and where it sits.
    }

    private bool _applyingIcon;

    private void ApplyIcon()
    {
        if (_applyingIcon) return;
        if (Content is string text) _label = text;
        if (Icon is null || _label is null) return;

        _applyingIcon = true;
        try
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.Space.Snug,
                Children =
                {
                    new FluentIcon { Data = Icon, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = _label, VerticalAlignment = VerticalAlignment.Center },
                },
            };
        }
        finally
        {
            _applyingIcon = false;
        }
    }
}

/// <summary>
/// One of <see cref="Icons"/>, drawn at its native grid and tinted with the text colour.
/// </summary>
/// <remarks>
/// The foreground is inherited, as text's is, so an icon in a button follows the button through
/// hover, press, disabled and accent without being told. Scaled from the 20 px grid rather than
/// stretched to its bounds, so every icon keeps the padding its designer gave it and they line
/// up with each other.
/// </remarks>
public sealed class FluentIcon : Control
{
    /// <summary>Path data from <see cref="Icons"/>.</summary>
    public static readonly StyledProperty<string?> DataProperty =
        AvaloniaProperty.Register<FluentIcon, string?>(nameof(Data));

    /// <summary>The tint. Inherited, like a text block's.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<FluentIcon>();

    /// <inheritdoc cref="DataProperty"/>
    public string? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <inheritdoc cref="ForegroundProperty"/>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    static FluentIcon() => AffectsRender<FluentIcon>(DataProperty, ForegroundProperty);

    /// <summary>Creates an icon at the standard size.</summary>
    public FluentIcon()
    {
        Width = Tokens.Chrome.Icon;
        Height = Tokens.Chrome.Icon;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is not { } brush) return;

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var scale = size / Icons.Size;
        var offset = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            context.DrawGeometry(brush, null, Icons.Geometry(Data));
        }
    }
}

/// <summary>
/// A row of mutually exclusive options in one well — the Windows 11 segmented control.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the rows of separate buttons Settings used to choose between two or three things,
/// which read as three actions rather than one choice. The selected segment is lifted onto the
/// card colour with a short accent pill under its label, as the Windows Community Toolkit's
/// Segmented control draws it.
/// </para>
/// <para>
/// Keyboard: Tab focuses the control, Left and Right move the selection. Each segment is a real
/// button, so screen readers announce it and a click is a click.
/// </para>
/// </remarks>
public sealed class SegmentedControl : Border
{
    private readonly Grid _row = new();
    private readonly List<Segment> _segments = [];
    private int _selected = -1;

    /// <summary>Raised when the user picks a different segment. Not raised by <see cref="Select"/>.</summary>
    public event EventHandler<int>? SelectionChanged;

    /// <summary>Builds the control with <paramref name="labels"/>, nothing selected.</summary>
    /// <param name="labels">One label per segment, in order.</param>
    public SegmentedControl(params string[] labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        CornerRadius = new CornerRadius(Tokens.Radius.Control + Tokens.Space.Hair);
        BorderThickness = new Thickness(Tokens.Border.Hairline);
        Padding = new Thickness(Tokens.Space.Hair);
        HorizontalAlignment = HorizontalAlignment.Left;
        Bind(BackgroundProperty, Tokens.Live.SurfaceMuted);
        Bind(BorderBrushProperty, Tokens.Live.Line);

        for (var i = 0; i < labels.Length; i++)
        {
            _row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var index = i;
            var segment = new Segment(labels[i]);
            segment.Click += (_, _) => Pick(index);
            segment.KeyDown += OnSegmentKey;
            Grid.SetColumn(segment, i);

            _segments.Add(segment);
            _row.Children.Add(segment);
        }

        Child = _row;
    }

    /// <summary>The segments, in order. Exposed for headless tests.</summary>
    public IReadOnlyList<Button> Segments => _segments;

    /// <summary>The selected segment, or -1.</summary>
    public int SelectedIndex => _selected;

    /// <summary>The selected segment's label, or null.</summary>
    public string? SelectedLabel => _selected >= 0 ? _segments[_selected].Label : null;

    /// <summary>Shows <paramref name="index"/> as selected without raising <see cref="SelectionChanged"/>.</summary>
    /// <param name="index">The segment, or -1 for none.</param>
    public void Select(int index)
    {
        _selected = index;
        for (var i = 0; i < _segments.Count; i++) _segments[i].IsSelected = i == index;
    }

    /// <summary>Selects a segment as the user would, raising <see cref="SelectionChanged"/>.</summary>
    /// <param name="index">The segment.</param>
    public void Pick(int index)
    {
        if (index < 0 || index >= _segments.Count) return;

        var changed = index != _selected;
        Select(index);
        if (changed) SelectionChanged?.Invoke(this, index);
    }

    private void OnSegmentKey(object? sender, KeyEventArgs e)
    {
        var step = e.Key switch { Key.Left => -1, Key.Right => 1, _ => 0 };
        if (step == 0) return;

        var next = Math.Clamp(_selected + step, 0, _segments.Count - 1);
        Pick(next);
        _segments[next].Focus(NavigationMethod.Directional);
        e.Handled = true;
    }

    /// <summary>One segment: a button that draws its own selected state.</summary>
    private sealed class Segment : Button
    {
        public static readonly StyledProperty<bool> IsSelectedProperty =
            AvaloniaProperty.Register<Segment, bool>(nameof(IsSelected));

        private readonly TextBlock _text;

        public Segment(string label)
        {
            Label = label;
            Background = Brushes.Transparent;
            BorderBrush = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            CornerRadius = new CornerRadius(Tokens.Radius.Control);
            // A little more below than above: the selection pill lives in the bottom padding,
            // clear of the label's descenders.
            Padding = new Thickness(
                Tokens.Space.Base + Tokens.Space.Tight, Tokens.Space.Tight,
                Tokens.Space.Base + Tokens.Space.Tight, Tokens.Space.Snug);
            MinHeight = 0;

            _text = new TextBlock
            {
                Text = label,
                FontFamily = Tokens.Fonts.Ui,
                FontSize = Tokens.Fonts.Body,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Content = _text;
            Restyle();
            ThemeAware.Watch(this, InvalidateVisual);
        }

        public string Label { get; }

        public bool IsSelected
        {
            get => GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        protected override Type StyleKeyOverride => typeof(Button);

        static Segment() => AffectsRender<Segment>(IsSelectedProperty);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsSelectedProperty) Restyle();
        }

        private void Restyle()
        {
            _text.FontWeight = IsSelected ? FontWeight.SemiBold : FontWeight.Normal;
            _text.Bind(TextBlock.ForegroundProperty, IsSelected ? Tokens.Live.Ink : Tokens.Live.InkMuted);
        }

        // Drawn beneath the label, which is a child; the template's own background is
        // transparent here, so this is the face.
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (!IsSelected || Bounds.Width <= 0 || Bounds.Height <= 0) return;

            var kind = ThemeResources.KindOf(ActualThemeVariant);
            var face = new RoundedRect(new Rect(Bounds.Size), Tokens.Radius.Control);

            context.DrawRectangle(
                BrushCache.Get(ThemeResources.Resolve(ColorToken.Surface, kind)),
                new Pen(BrushCache.Get(ThemeResources.Resolve(ColorToken.Line, kind)), Tokens.Border.Hairline),
                face);

            // The accent pill under the label: Windows' selection indicator, horizontal.
            var pill = new Rect(
                (Bounds.Width - PillWidth) / 2, Bounds.Height - PillHeight - Tokens.Space.Hair,
                PillWidth, PillHeight);
            context.DrawRectangle(
                BrushCache.Get(ThemeResources.Resolve(ColorToken.Accent, kind)), null,
                new RoundedRect(pill, PillHeight / 2));
        }

        private const double PillWidth = 16;
        private const double PillHeight = 3;
    }
}

/// <summary>
/// A small round icon button for the floating bar — the cancel target.
/// </summary>
/// <remarks>
/// Drawn by hand rather than by the Fluent theme: the bar is dark in every theme, and a themed
/// button would draw light-theme hover fills on it.
/// </remarks>
public sealed class BarIconButton : Button
{
    /// <summary>The icon, from <see cref="Icons"/>.</summary>
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<BarIconButton, string?>(nameof(Icon));

    /// <inheritdoc cref="IconProperty"/>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    static BarIconButton() => AffectsRender<BarIconButton>(IsPointerOverProperty, IsPressedProperty, IconProperty);

    /// <summary>Creates the button.</summary>
    public BarIconButton()
    {
        Width = Size;
        Height = Size;
        Padding = new Thickness(0);
        Background = null;
        BorderThickness = new Thickness(0);
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
        Template = null;
    }

    /// <summary>Diameter of the hit target.</summary>
    public const double Size = 22;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0) return;

        var fill = IsPressed ? 0.22 : IsPointerOver ? 0.14 : 0.08;
        context.DrawEllipse(BrushCache.Get(Tokens.Colors.BarInk, fill), null, bounds.Center, bounds.Width / 2, bounds.Height / 2);

        if (Icon is null) return;

        const double glyph = 12;
        var scale = glyph / Icons.Size;
        var offset = (bounds.Width - glyph) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset, offset)))
        {
            context.DrawGeometry(BrushCache.Get(Tokens.Colors.BarInk), null, Icons.Geometry(Icon));
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // The bar captures the pointer to tell a click from a drag; this button's own click
        // must not also count as a click on the bar underneath it.
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
    }

    /// <summary>Clicks the button, for tests.</summary>
    public void Invoke() => RaiseEvent(new RoutedEventArgs(ClickEvent));
}
