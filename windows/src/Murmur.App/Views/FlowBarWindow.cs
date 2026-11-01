using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Abstractions;
using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>
/// The floating bar — a small dark capsule that lives above every other window, bottom centre
/// by default.
/// </summary>
/// <remarks>
/// <para>
/// This, not the hub, is Murmur's primary interface. A dictation app is used <i>while</i> you
/// are working in something else, so the thing that shows whether it is listening has to be
/// visible without leaving that something else. A full application window cannot be, and the
/// previous design's always-on-screen front panel was solving the wrong problem.
/// </para>
/// <para>
/// Three properties make it work, and all three are load-bearing:
/// </para>
/// <list type="bullet">
/// <item><c>WindowDecorations.None</c> plus a transparent background and a rounded child, so
/// the window <i>is</i> the pill rather than a pill inside a frame.</item>
/// <item><c>ShowActivated = false</c>. <b>This one is not cosmetic.</b> A dictation app types
/// into whatever has focus, so a bar that takes focus when it appears or when it is clicked
/// would redirect the user's own words into itself.</item>
/// <item><c>ShowInTaskbar = false</c> with <c>Topmost</c>, so it behaves like an indicator
/// rather than an application.</item>
/// </list>
/// <para>
/// It says only what it has to. At rest: a dim dot. Recording: the red dot, live level bars, and a
/// cancel target. Transcribing: the word "Transcribing" and nothing that spins. The dot and the
/// body change colour at once; the word fades in over 140 ms, which is the only motion apart from
/// the level bars, and nothing loops.
/// </para>
/// <para>
/// Beside the dot sits the privacy badge — a laptop when Parakeet on this PC hears you, a cloud
/// when your Azure resource does, a struck-out cloud when Azure was chosen but Parakeet had to
/// answer. It is drawn in the bar's muted ink in every case: where your voice went is information,
/// never an alarm, and red belongs to recording alone. When something did not go to plan — a
/// fallback, a failed dictation — one short sentence takes the label's place for a few seconds
/// at rest and goes again; nothing has to be dismissed.
/// </para>
/// <para>
/// Nothing here runs on a timer. State arrives as <c>DictationEngine.StateChanged</c> — a handful
/// of events per dictation — and model presence as <c>ModelLocator.Changed</c>, each posted to
/// the UI thread. The level is the one value that moves continuously, and it is not pushed at
/// all: the level bars read it on their own frame clock, which runs only while they are
/// actually moving. A bar sitting idle on screen therefore costs no wakeups. It used to poll the
/// engine and the disk every 60 ms for the whole life of the process.
/// </para>
/// </remarks>
public sealed class FlowBarWindow : Window
{
    private readonly Composition? _composition;
    private readonly AppSettings? _settings;

    private readonly Border _pill;
    private readonly StatusDot _dot;
    private readonly FluentIcon _badge;
    private readonly WaveBars _wave;
    private readonly TextBlock _label;
    private readonly BarIconButton _cancel;
    private readonly StackPanel _content;

    private Point _pressOrigin;
    private bool _pressed;
    private bool _dragging;
    private BarState _state = BarState.Idle;

    /// <summary>Whether the last dictation was answered by the fallback; cleared by one that was not.</summary>
    private bool _fellBack;

    /// <summary>A sentence shown at rest after something went other than planned, or null.</summary>
    private string? _notice;
    private bool _noticeIsAlert;
    private IDisposable? _noticeTimer;

    /// <summary>Raised when the user asks for the hub, from the bar's menu or a double click.</summary>
    public event EventHandler? HubRequested;

    /// <summary>Raised when the user asks for settings.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the user quits from the bar's menu.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Builds a bar with no engine behind it. Used by headless tests and previews.</summary>
    public FlowBarWindow() : this(null) { }

    /// <summary>Builds the bar over <paramref name="composition"/>.</summary>
    /// <param name="composition">The object graph, or null for a preview.</param>
    public FlowBarWindow(Composition? composition)
    {
        _composition = composition;
        _settings = composition?.Settings;

        Title = "Murmur";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = false;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Background = Brushes.Transparent;

        // Plain per-pixel transparency, not acrylic: a blur behind an always-on-top window is
        // re-sampled every time anything under it changes, which on a bar parked over a video
        // call is all the time. The body is 92 % opaque instead, so a hint of what is under it
        // still shows at the edge for nothing.
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SizeToContent = SizeToContent.Manual;

        _dot = new StatusDot
        {
            DotColor = Tokens.Colors.BarDotIdle,
            UnlitOpacity = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _badge = new FluentIcon
        {
            Width = Tokens.Bar.BadgeSize,
            Height = Tokens.Bar.BadgeSize,
            Foreground = Tokens.Brushes.BarInkMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _wave = new WaveBars
        {
            VerticalAlignment = VerticalAlignment.Stretch,
            Height = Tokens.Bar.WaveHeight,
            IsVisible = false,
        };

        // Read per animation frame, and only while the bars are moving: throttled to the
        // display by construction, and nothing at all when idle.
        if (composition?.Engine is { } levelSource) _wave.LevelSource = () => levelSource.Level;

        _label = new TextBlock
        {
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Label,
            FontWeight = FontWeight.SemiBold,
            Foreground = Tokens.Brushes.BarInk,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Opacity = 0,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = Tokens.Motion.Fade }],
        };

        _cancel = new BarIconButton
        {
            Icon = Icons.Dismiss,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _cancel.Click += (_, _) => Toggle();

        _content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _dot, _badge, _wave, _label, _cancel },
        };

        _pill = new Border
        {
            Background = Tokens.Brushes.BarBody,
            CornerRadius = new CornerRadius(Tokens.Radius.Pill),
            BorderBrush = BrushCache.Get(Tokens.Colors.BarEdge),
            BorderThickness = new Thickness(Tokens.Border.Hairline),
            Padding = new Thickness(Tokens.Bar.PaddingLong, Tokens.Bar.PaddingShort),
            Cursor = new Cursor(StandardCursorType.Hand),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = Tokens.Bar.ShadowOffset,
                Blur = Tokens.Bar.ShadowBlur,
                Color = Color.FromArgb(
                    (byte)(Tokens.Bar.ShadowOpacity * 255), 0, 0, 0),
            }),
            Child = _content,
        };

        _pill.PointerPressed += OnPillPressed;
        _pill.PointerMoved += OnPillMoved;
        _pill.PointerReleased += OnPillReleased;

        Content = new Panel { Margin = new Thickness(0), Children = { _pill } };
        ContextMenu = BuildMenu();

        Edge = _settings?.Data.FlowBarEdge ?? ScreenEdge.Bottom;
        Offset = _settings?.Data.FlowBarOffset ?? FlowBarPlacement.DefaultOffset;

        var readiness = _composition?.Readiness ?? EngineReadiness.Ready;
        ApplyState(
            readiness == EngineReadiness.Ready ? BarState.Idle : BarState.Unavailable,
            EngineStatus.ShortLabel(readiness));

        // Settings, the model on disk and the key all feed readiness; one event covers the three.
        if (_composition is not null) _composition.ReadinessChanged += OnReadinessChanged;

        if (_composition?.Engine is not null)
        {
            _composition.Engine.StateChanged += OnEngineStateChanged;
            _composition.Engine.CaptureFaulted += OnCaptureFaulted;
            _composition.Engine.Completed += OnCompleted;
            _composition.Engine.TranscriptionFaulted += OnTranscriptionFaulted;
            _composition.Engine.Start();
        }

        if (_composition?.Gesture is not null) _composition.Gesture.HandsFreeChanged += OnHandsFreeChanged;
        if (_composition?.Transcriber is not null) _composition.Transcriber.Fallback.FellBack += OnFellBack;
    }

    /// <summary>Which edge the bar is docked to.</summary>
    public ScreenEdge Edge { get; private set; }

    /// <summary>How far along that edge, 0…1.</summary>
    public double Offset { get; private set; }

    /// <summary>What the bar is showing. Exposed for headless tests.</summary>
    public BarState State => _state;

    /// <summary>The word the bar shows, or empty. Exposed for headless tests.</summary>
    public string Label => _label.Text ?? string.Empty;

    /// <summary>The level display. Exposed for headless tests.</summary>
    public WaveBars Wave => _wave;

    /// <summary>The state dot. Exposed for headless tests.</summary>
    public StatusDot Dot => _dot;

    /// <summary>The cancel target, shown while recording. Exposed for headless tests.</summary>
    public BarIconButton Cancel => _cancel;

    /// <summary>Where the badge says your voice goes. Exposed for headless tests.</summary>
    public PrivacyBadge Badge { get; private set; }

    /// <summary>What the badge says on hover. Exposed for headless tests.</summary>
    public string BadgeText => ToolTip.GetTip(_badge) as string ?? string.Empty;

    /// <summary>The notice on show, or null. Exposed for headless tests.</summary>
    public string? Notice => _notice;

    /// <summary>The pill itself, so a preview can be rendered without a window manager.</summary>
    public Border Pill => _pill;

    // ---- State ----

    /// <summary>
    /// Puts the bar into <paramref name="state"/> and resizes it to suit.
    /// </summary>
    /// <remarks>
    /// The window is resized rather than kept at its widest and padded with transparency: a
    /// transparent region of an always-on-top window still swallows clicks meant for whatever
    /// is underneath it, which on a bar that sits over other people's applications all day is
    /// not a small thing.
    /// </remarks>
    /// <param name="state">What to show.</param>
    /// <param name="unavailableLabel">
    /// For <see cref="BarState.Unavailable"/>, what stands in the way — "Add API key", "Key
    /// rejected" — from <see cref="EngineStatus.ShortLabel"/>; null means "No model".
    /// </param>
    public void ApplyState(BarState state, string? unavailableLabel = null)
    {
        _state = state;
        _unavailableLabel = state == BarState.Unavailable ? unavailableLabel ?? "No model" : null;

        // A notice belongs to rest; starting to talk again puts it away.
        if (state is BarState.Listening or BarState.HandsFree) ClearNotice(sync: false);

        var listening = state is BarState.Listening or BarState.HandsFree;

        // Level bars only while recording: at rest there is no level to show, and a row of bars
        // sitting still reads as "listening, but you're quiet". Hidden, they also ask for no
        // frames at all.
        _wave.IsActive = listening;
        _wave.IsVisible = listening;
        _cancel.IsVisible = listening;

        _dot.IsLit = listening;
        _dot.DotColor = state switch
        {
            BarState.Listening or BarState.HandsFree => Tokens.Colors.BarLive,
            BarState.Processing => Tokens.Colors.BarInkMuted,
            BarState.Unavailable => Tokens.Colors.BarNotice,
            _ when _notice is not null && _noticeIsAlert => Tokens.Colors.BarNotice,
            _ => Tokens.Colors.BarDotIdle,
        };

        // Unavailable has nothing to say about where audio would go: nothing will be heard.
        _badge.IsVisible = state != BarState.Unavailable;
        RefreshBadge();

        // Unavailable keeps its dot visible: "nothing will happen if you press the key" has to
        // be legible from the bar, whichever engine is the one that cannot answer.
        if (state == BarState.Unavailable) _dot.IsLit = true;

        // Only the states that need explaining carry a word. At rest the shape is the label.
        // "Transcribing" stands alone — no spinner, no dots: a word that is there is enough, and
        // a looping animation on a window that never closes is not free.
        _label.Text = state switch
        {
            BarState.Processing => "Transcribing",
            BarState.Unavailable => _unavailableLabel,
            BarState.HandsFree => "Hands-free — tap to stop",
            BarState.Idle => _notice ?? string.Empty,
            _ => string.Empty,
        };
        _label.Foreground = state == BarState.Idle ? Tokens.Brushes.BarInkMuted : Tokens.Brushes.BarInk;

        _pill.Background = BrushCache.Get(
            listening ? Tokens.Colors.BarBodyActive : Tokens.Colors.BarBody);

        Resize();
    }

    /// <summary>The pill's length along its long axis, for the current state.</summary>
    /// <remarks>
    /// Hands-free and a notice carry words of their own, so they are measured — once, on the state
    /// change, never per frame — and a side-docked bar, which shows no words, keeps the fixed size.
    /// </remarks>
    public double PillLength => _state switch
    {
        BarState.Listening => Tokens.Bar.ListeningWidth,
        BarState.HandsFree when FlowBarPlacement.IsHorizontal(Edge) =>
            Tokens.Bar.ListeningWidth + Tokens.Space.Snug + LabelWidth(),
        BarState.HandsFree => Tokens.Bar.ListeningWidth,
        BarState.Processing or BarState.Unavailable => Tokens.Bar.ProcessingWidth,
        BarState.Idle when _notice is not null && FlowBarPlacement.IsHorizontal(Edge) => Math.Min(
            Tokens.Bar.NoticeMaxWidth,
            (Tokens.Bar.PaddingLong * 2) + Tokens.Bar.DotSize + Tokens.Bar.BadgeSize + (Tokens.Space.Snug * 2)
                + LabelWidth() + Tokens.Bar.PaddingShort),
        _ => Tokens.Bar.IdleWidth,
    };

    /// <summary>The width the label's current text needs.</summary>
    private double LabelWidth()
    {
        _label.Measure(Size.Infinity);
        return Math.Ceiling(_label.DesiredSize.Width);
    }

    private void Resize()
    {
        var horizontal = FlowBarPlacement.IsHorizontal(Edge);

        // A side-docked bar has no room for a word, so the label goes and the dot carries the
        // state on its own. Decided first: a hidden label measures as nothing, and the length
        // below is measured from it.
        ShowLabel(horizontal && !string.IsNullOrEmpty(_label.Text));

        Width = horizontal ? PillLength : Tokens.Bar.Height;
        Height = horizontal ? Tokens.Bar.Height : PillLength;

        _content.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        _wave.IsVertical = !horizontal;

        if (horizontal)
        {
            _wave.Width = double.NaN;
            _wave.Height = Tokens.Bar.WaveHeight;
            _pill.Padding = new Thickness(Tokens.Bar.PaddingLong, Tokens.Bar.PaddingShort);
        }
        else
        {
            _wave.Width = Tokens.Bar.WaveHeight;
            _wave.Height = double.NaN;
            _pill.Padding = new Thickness(Tokens.Bar.PaddingShort, Tokens.Bar.PaddingLong);
        }

        MoveToDock();
    }

    /// <summary>Shows or hides the word, fading it in.</summary>
    private void ShowLabel(bool visible)
    {
        _label.IsVisible = visible;
        _label.Opacity = visible ? 1 : 0;
    }

    // ---- Engine ----

    /// <summary>Puts the bar into whatever state the engine and its setup are in now.</summary>
    /// <remarks>
    /// Reads the engine's current state and readiness rather than trusting the event that
    /// prompted it, so posts that arrive out of order or late still converge on the truth. At
    /// rest the bar says what is in the way for the engine actually chosen: no model for
    /// Parakeet, a missing or rejected key for Azure — not "No model" to someone who never
    /// wanted one.
    /// </remarks>
    private void SyncFromEngine()
    {
        if (_composition is null) return;

        var readiness = _composition.Readiness;

        var wanted = _composition.Engine?.State switch
        {
            DictationState.Recording when _composition.Gesture?.IsHandsFree == true => BarState.HandsFree,
            DictationState.Recording => BarState.Listening,
            DictationState.Transcribing => BarState.Processing,
            _ => readiness == EngineReadiness.Ready ? BarState.Idle : BarState.Unavailable,
        };

        var label = wanted == BarState.Unavailable ? EngineStatus.ShortLabel(readiness) ?? "No model" : null;
        if (wanted != _state || !string.Equals(label, _unavailableLabel, StringComparison.Ordinal))
        {
            ApplyState(wanted, label);
        }
        else
        {
            // The engine mode may have changed under an unchanged state; the badge follows it.
            RefreshBadge();
        }
    }

    // ---- Badge and notices ----

    /// <summary>Points the badge at whoever will hear — or just heard — the user.</summary>
    private void RefreshBadge()
    {
        var data = _settings?.Data ?? new SettingsData();

        // A fallback is remembered only while it could happen again: in Parakeet mode there is
        // nothing to fall back from.
        if (data.Engine != EngineMode.AzureWithParakeetFallback) _fellBack = false;

        Badge = _fellBack ? PrivacyBadge.OfflineFallback : BarNotices.BadgeFor(data.Engine);
        _badge.Data = Badge switch
        {
            PrivacyBadge.Cloud => Icons.Cloud,
            PrivacyBadge.OfflineFallback => Icons.CloudOff,
            _ => Icons.Laptop,
        };

        var text = BarNotices.Describe(Badge, data.Language);
        ToolTip.SetTip(_badge, text);
        Avalonia.Automation.AutomationProperties.SetName(_badge, text);
    }

    /// <summary>Records which engine heard the last dictation. Exposed for headless tests.</summary>
    /// <param name="engine">The engine that answered.</param>
    /// <param name="fellBack">Whether it was the fallback.</param>
    internal void ShowOutcome(TranscriberKind engine, bool fellBack)
    {
        var mode = _settings?.Data.Engine ?? EngineMode.Parakeet;
        _fellBack = BarNotices.BadgeFor(engine, fellBack, mode) == PrivacyBadge.OfflineFallback;
        RefreshBadge();
    }

    /// <summary>Shows a sentence at rest for <see cref="Tokens.Motion.NoticeHold"/>. Exposed for headless tests.</summary>
    /// <param name="text">What to say.</param>
    /// <param name="alert">
    /// True when a dictation was lost, which lights the dot in the notice colour; false for news the
    /// user need not act on, which stays in neutral ink.
    /// </param>
    internal void ShowNotice(string text, bool alert)
    {
        _noticeTimer?.Dispose();
        _notice = text;
        _noticeIsAlert = alert;

        // One shot, cancelled by the next notice or the next recording. At rest nothing is armed.
        _noticeTimer = DispatcherTimer.RunOnce(() => ClearNotice(sync: true), Tokens.Motion.NoticeHold);

        if (_state == BarState.Idle) ApplyState(BarState.Idle);
    }

    /// <summary>Puts the notice away.</summary>
    internal void ClearNotice(bool sync)
    {
        _noticeTimer?.Dispose();
        _noticeTimer = null;
        if (_notice is null) return;

        _notice = null;
        _noticeIsAlert = false;
        if (sync && _state == BarState.Idle) ApplyState(BarState.Idle);
    }

    /// <summary>Raised on the processing thread for every finished dictation.</summary>
    private void OnCompleted(object? sender, DictationResult result) =>
        Dispatcher.UIThread.Post(() => ShowOutcome(result.Engine, result.FellBack));

    /// <summary>Raised on the transcribing thread when Parakeet answered for Azure.</summary>
    private void OnFellBack(object? sender, TranscriptionFallback fallback)
    {
        var language = _settings?.Data.Language ?? LanguageMode.Auto;
        var text = BarNotices.ForFallback(fallback, language);
        Dispatcher.UIThread.Post(() => ShowNotice(text, alert: false));
    }

    /// <summary>Raised on the processing thread when a dictation could not be transcribed.</summary>
    private void OnTranscriptionFaulted(object? sender, Exception failure)
    {
        var text = BarNotices.ForFault(failure);
        Dispatcher.UIThread.Post(() => ShowNotice(text, alert: true));
    }

    private void OnHandsFreeChanged(object? sender, bool handsFree) => Dispatcher.UIThread.Post(SyncFromEngine);

    /// <summary>The word shown while unavailable, so a change of reason is noticed.</summary>
    private string? _unavailableLabel;

    /// <summary>Raised on the engine's thread; the bar is touched only on the UI thread.</summary>
    private void OnEngineStateChanged(object? sender, DictationState state) =>
        Dispatcher.UIThread.Post(SyncFromEngine);

    /// <summary>Settings, the model or the key changed, on whichever thread changed it.</summary>
    private void OnReadinessChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(SyncFromEngine);

    /// <summary>Starts or stops dictation. The hotkey remains the primary trigger.</summary>
    public void Toggle()
    {
        if (_composition?.Engine is null)
        {
            // No engine — a headless test or a machine with no platform layer. The bar still
            // cycles so its states can be exercised and rendered.
            ApplyState(_state == BarState.Listening ? BarState.Idle : BarState.Listening);
            return;
        }

        _composition.Engine.TogglePushToTalk();
        SyncFromEngine();
    }

    /// <summary>
    /// Shows a capture failure on the bar rather than letting it vanish silently.
    /// </summary>
    /// <remarks>
    /// Fires on a background thread, so the change is marshalled to the dispatcher. The full
    /// exception is in %LocalAppData%\Murmur\last-capture-error.log.
    /// </remarks>
    private void OnCaptureFaulted(object? sender, Exception ex) =>
        Dispatcher.UIThread.Post(() =>
        {
            ApplyState(BarState.Unavailable);
            _label.Text = "Mic error";
            ShowLabel(FlowBarPlacement.IsHorizontal(Edge));
        });

    // ---- Dragging and docking ----

    /// <summary>How far the pointer must travel before a press becomes a drag.</summary>
    private const double DragThreshold = 5;

    /// <remarks>
    /// The pointer is captured rather than handed to <c>BeginMoveDrag</c>. The native move-drag
    /// takes the pointer away for the whole gesture, so no release event ever comes back — and
    /// without a release there is nothing to distinguish a click from a drag, and nothing to
    /// hang the snap on. Moving the window by hand costs a few lines and keeps both.
    /// </remarks>
    private void OnPillPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _pressOrigin = e.GetPosition(this);
        _pressed = true;
        _dragging = false;
        e.Pointer.Capture(_pill);
    }

    private void OnPillMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;

        if (!_dragging)
        {
            var moved = e.GetPosition(this) - _pressOrigin;
            if (Math.Abs(moved.X) + Math.Abs(moved.Y) < DragThreshold) return;

            _dragging = true;
        }

        // The grab point inside the window is fixed for the whole gesture, so anchoring the
        // window to it is stable — unlike a per-frame delta, which feeds the window's own
        // movement back into the next measurement.
        var scale = Scaling();
        var pointer = this.PointToScreen(e.GetPosition(this));

        Position = new PixelPoint(
            pointer.X - (int)Math.Round(_pressOrigin.X * scale),
            pointer.Y - (int)Math.Round(_pressOrigin.Y * scale));
    }

    private void OnPillReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;

        _pressed = false;
        e.Pointer.Capture(null);

        // Let go over an edge and it settles there; let go without having moved and it was a
        // click, which is how you start dictating from the bar.
        if (_dragging) SnapToNearestEdge();
        else Toggle();
    }

    /// <summary>Docks the bar to whichever edge it is nearest, and remembers where.</summary>
    public void SnapToNearestEdge()
    {
        _dragging = false;

        var area = WorkArea();
        var bar = new PlacementRect(PositionInDips().X, PositionInDips().Y, Width, Height);

        var edge = FlowBarPlacement.NearestEdge(area, bar);
        DockTo(edge, FlowBarPlacement.OffsetFor(area, bar, edge));
    }

    /// <summary>Docks the bar to <paramref name="edge"/>, keeping its position along it.</summary>
    /// <param name="edge">The edge to dock to.</param>
    public void DockTo(ScreenEdge edge) => DockTo(edge, Offset);

    /// <summary>Docks the bar and persists the placement.</summary>
    /// <param name="edge">The edge to dock to.</param>
    /// <param name="offset">How far along it, 0…1.</param>
    public void DockTo(ScreenEdge edge, double offset)
    {
        var reoriented = FlowBarPlacement.IsHorizontal(edge) != FlowBarPlacement.IsHorizontal(Edge);

        Edge = edge;
        Offset = Math.Clamp(offset, 0, 1);

        if (reoriented) Resize();
        else MoveToDock();

        Persist();
    }

    private void Persist()
    {
        if (_settings is null) return;
        if (_settings.Data.FlowBarEdge == Edge &&
            Math.Abs(_settings.Data.FlowBarOffset - Offset) < 0.001)
        {
            return;
        }

        // Writing settings must never take the app down: the bar is dragged constantly and the
        // file could be locked by an editor, a backup agent or a second copy of the app.
        try
        {
            _settings.Update(_settings.Data with { FlowBarEdge = Edge, FlowBarOffset = Offset });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Saving the bar position failed", ex);
        }
    }

    /// <summary>Moves the window to the position its edge and offset describe.</summary>
    public void MoveToDock()
    {
        var area = WorkArea();
        var (x, y) = FlowBarPlacement.Position(area, Width, Height, Edge, Offset);
        var scale = Scaling();

        Position = new PixelPoint((int)Math.Round(x * scale), (int)Math.Round(y * scale));
    }

    /// <summary>
    /// The usable screen area in device-independent pixels.
    /// </summary>
    /// <remarks>
    /// Work area, not bounds: docking a bar to the bottom of the <i>screen</i> puts it behind
    /// the taskbar. Falls back to a plausible 1080p desktop when there is no screen at all,
    /// which is the headless case.
    /// </remarks>
    public PlacementRect WorkArea()
    {
        var screens = Screens;
        var screen = screens is null || screens.ScreenCount == 0
            ? null
            : screens.ScreenFromPoint(Position) ?? screens.Primary;

        if (screen is null) return new PlacementRect(0, 0, 1920, 1040);

        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var area = screen.WorkingArea;

        return new PlacementRect(area.X / scale, area.Y / scale, area.Width / scale, area.Height / scale);
    }

    private double Scaling()
    {
        var screens = Screens;
        var screen = screens is null || screens.ScreenCount == 0 ? null : screens.Primary;
        var scale = screen?.Scaling ?? 1;
        return scale <= 0 ? 1 : scale;
    }

    private PlacementRect PositionInDips()
    {
        var scale = Scaling();
        return new PlacementRect(Position.X / scale, Position.Y / scale, Width, Height);
    }

    // ---- Menu ----

    /// <summary>
    /// The bar's right-click menu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything reachable from the bar is here, because the bar has no room for chrome.
    /// </para>
    /// <para>
    /// The microphone submenu is rebuilt every time the menu opens rather than once at startup.
    /// Devices appear and disappear while the app is running — that is what a USB headset is —
    /// and a list captured at launch would be stale by the time anyone right-clicked it.
    /// </para>
    /// </remarks>
    private ContextMenu BuildMenu()
    {
        var hub = new MenuItem { Header = "Open Murmur" };
        hub.Click += (_, _) => HubRequested?.Invoke(this, EventArgs.Empty);

        var history = new MenuItem { Header = "Transcript history" };
        history.Click += (_, _) => HubRequested?.Invoke(this, EventArgs.Empty);

        // Copy, not paste: the click has just taken focus from the window the text was for.
        _copyLast = new MenuItem { Header = "Copy last transcript" };
        _copyLast.Click += async (_, _) => await CopyLastAsync();

        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var bottom = new MenuItem { Header = "Dock to bottom" };
        bottom.Click += (_, _) => DockTo(ScreenEdge.Bottom);

        var left = new MenuItem { Header = "Dock to left" };
        left.Click += (_, _) => DockTo(ScreenEdge.Left);

        var right = new MenuItem { Header = "Dock to right" };
        right.Click += (_, _) => DockTo(ScreenEdge.Right);

        var hide = new MenuItem { Header = "Hide bar" };
        hide.Click += (_, _) => HideBar();

        var quit = new MenuItem { Header = "Quit Murmur" };
        quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        _microphoneMenu = new MenuItem { Header = "Microphone" };
        _cloudModelMenu = new MenuItem { Header = "Cloud model" };

        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                hub, history, _copyLast, settings,
                new Separator(),
                _microphoneMenu,
                _cloudModelMenu,
                new Separator(),
                bottom, left, right,
                new Separator(),
                hide, quit,
            },
        };

        menu.Opening += (_, _) =>
        {
            RebuildMicrophoneMenu();
            RebuildCloudModelMenu();
            _copyLast.IsEnabled = _composition?.PasteLast.Text is not null;

            // The one cheap moment to look at the disk again: the user is already here, and a
            // right-click is the natural thing to try after installing the model.
            _composition?.Models.Refresh();
        };
        return menu;
    }

    private MenuItem? _microphoneMenu;
    private MenuItem? _cloudModelMenu;
    private MenuItem _copyLast = null!;

    /// <summary>
    /// Puts the last transcript on the clipboard, as the user's own copy. Shared with the tray.
    /// </summary>
    /// <returns>Whether there was one to copy.</returns>
    /// <remarks>
    /// An ordinary clipboard write, which lands in clipboard history: the user asked for it, as
    /// they do with History's Copy. Only the injector's transient writes are kept out of it.
    /// </remarks>
    public async Task<bool> CopyLastAsync()
    {
        if (_composition?.PasteLast.Text is not { } text || Clipboard is not { } clipboard) return false;

        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            // An async void click handler sits above this; nothing may escape it.
            CrashLog.Write("Copying the last transcript failed", ex);
            return false;
        }
    }

    /// <summary>The "Cloud model" submenu. Exposed for headless tests.</summary>
    internal MenuItem? CloudModelMenu => _cloudModelMenu;

    /// <summary>
    /// Fills the "Cloud model" submenu: Accurate and Fast, the one in use ticked.
    /// </summary>
    /// <remarks>
    /// The tick follows <see cref="AzureProfiles.EffectiveProfile"/> rather than the stored
    /// choice, so with only one deployment filled in it marks the one that will really answer.
    /// A profile with no deployment is shown but disabled. The choice applies to the next
    /// dictation; nothing is reloaded.
    /// </remarks>
    internal void RebuildCloudModelMenu()
    {
        if (_cloudModelMenu is null) return;

        var data = _settings?.Data ?? new SettingsData();
        var active = AzureProfiles.EffectiveProfile(data);
        var items = new List<object>();

        foreach (var profile in new[] { CloudProfile.Accurate, CloudProfile.Fast })
        {
            var item = new MenuItem
            {
                Header = $"{AzureProfiles.Label(profile)} ({AzureProfiles.ModelFor(profile)})",
                Icon = Tick(profile == active && AzureProfiles.DeploymentFor(data, profile) is not null),
                IsEnabled = AzureProfiles.DeploymentFor(data, profile) is not null,
            };

            var chosen = profile;
            item.Click += (_, _) => ChooseCloudModel(chosen);
            items.Add(item);
        }

        _cloudModelMenu.ItemsSource = items;
    }

    private void ChooseCloudModel(CloudProfile profile)
    {
        if (_settings is null) return;

        try
        {
            AzureProfiles.Select(_settings, profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Saving the cloud model choice failed", ex);
        }
    }

    /// <summary>
    /// Fills the microphone submenu with whatever is plugged in right now.
    /// </summary>
    /// <remarks>
    /// "System default" is always offered and always first — it is the only entry that keeps
    /// working when a device is unplugged, and it is where a user who has never thought about
    /// this should be. The current choice is marked rather than merely highlighted, because a
    /// context menu has no selection state of its own.
    /// </remarks>
    private void RebuildMicrophoneMenu()
    {
        if (_microphoneMenu is null) return;

        var chosen = _settings?.Data.MicrophoneId;
        var devices = _composition?.Microphones?.List() ?? [];
        var items = new List<object>();

        var systemDefault = new MenuItem
        {
            Header = "System default",
            Icon = Tick(chosen is null),
        };
        systemDefault.Click += (_, _) => ChooseMicrophone(null);
        items.Add(systemDefault);

        if (devices.Count > 0) items.Add(new Separator());

        foreach (var device in devices)
        {
            var item = new MenuItem
            {
                Header = device.Name,
                Icon = Tick(string.Equals(chosen, device.Id, StringComparison.Ordinal)),
            };

            var id = device.Id;
            item.Click += (_, _) => ChooseMicrophone(id);
            items.Add(item);
        }

        if (devices.Count == 0)
        {
            items.Add(new MenuItem { Header = "No microphones found", IsEnabled = false });
        }

        _microphoneMenu.ItemsSource = items;
    }

    private static FluentIcon? Tick(bool selected) => selected
        ? new FluentIcon { Data = Icons.CheckmarkCircle }
        : null;

    private void ChooseMicrophone(string? id)
    {
        if (_settings is null) return;
        if (string.Equals(_settings.Data.MicrophoneId, id, StringComparison.Ordinal)) return;

        try
        {
            _settings.Update(_settings.Data with { MicrophoneId = id });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Saving the microphone choice failed", ex);
        }
    }

    /// <summary>
    /// Hides the bar and remembers that it is hidden.
    /// </summary>
    /// <remarks>
    /// The hotkey and the tray icon keep working — the bar is an indicator, never the thing
    /// that makes dictation possible — and the tray menu is how it comes back.
    /// </remarks>
    public void HideBar()
    {
        Hide();

        if (_settings is null) return;

        try
        {
            _settings.Update(_settings.Data with { ShowFlowBar = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Saving the bar visibility failed", ex);
        }
    }

    /// <summary>Shows the bar again and records that it is wanted.</summary>
    public void ShowBar()
    {
        if (_settings is not null && !_settings.Data.ShowFlowBar)
        {
            try
            {
                _settings.Update(_settings.Data with { ShowFlowBar = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashLog.Write("Saving the bar visibility failed", ex);
            }
        }

        Show();
        MoveToDock();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The visibility preference is applied here rather than at startup: the desktop lifetime
    /// shows its <c>MainWindow</c> <i>after</i> <c>OnFrameworkInitializationCompleted</c>
    /// returns, so hiding the bar from there is undone a moment later.
    /// </para>
    /// <para>
    /// The transparency fallback is here for the same reason — the actual level is only known
    /// once the window exists. If the compositor refuses a transparent window the pill would
    /// otherwise sit in an opaque black rectangle, so the window background is filled with the
    /// pill's own colour and the corners squared off, which degrades to a plain dark bar rather
    /// than to something visibly broken.
    /// </para>
    /// </remarks>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (ActualTransparencyLevel == WindowTransparencyLevel.None)
        {
            var body = Tokens.Colors.BarBody;
            Background = BrushCache.Get(Color.FromRgb(body.R, body.G, body.B));
            _pill.BoxShadow = default;
        }

        MoveToDock();

        if (_settings is not null && !_settings.Data.ShowFlowBar) Hide();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Closing the bar leaves Murmur running in the tray, exactly as closing the old window
    /// did. Genuinely closing an Avalonia window destroys it, and <c>Show()</c> on a destroyed
    /// window throws — which is what used to take the whole app down when "Show Murmur" was
    /// clicked in the tray afterwards. A user-initiated close is therefore a hide; application
    /// shutdown still closes for real.
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_composition is not null && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            HideBar();
        }

        base.OnClosing(e);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        if (_composition is not null) _composition.ReadinessChanged -= OnReadinessChanged;

        if (_composition?.Engine is not null)
        {
            _composition.Engine.StateChanged -= OnEngineStateChanged;
            _composition.Engine.CaptureFaulted -= OnCaptureFaulted;
            _composition.Engine.Completed -= OnCompleted;
            _composition.Engine.TranscriptionFaulted -= OnTranscriptionFaulted;
        }

        if (_composition?.Gesture is not null) _composition.Gesture.HandsFreeChanged -= OnHandsFreeChanged;
        if (_composition?.Transcriber is not null) _composition.Transcriber.Fallback.FellBack -= OnFellBack;

        _noticeTimer?.Dispose();
        base.OnClosed(e);
    }
}
