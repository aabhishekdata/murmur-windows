using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Core;

namespace Murmur.App.Views;

/// <summary>
/// The floating bar — a small pill that lives above every other window.
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
/// <item><c>SystemDecorations.None</c> plus a transparent background and a rounded child, so
/// the window <i>is</i> the pill rather than a pill inside a frame.</item>
/// <item><c>ShowActivated = false</c>. <b>This one is not cosmetic.</b> A dictation app types
/// into whatever has focus, so a bar that takes focus when it appears or when it is clicked
/// would redirect the user's own words into itself.</item>
/// <item><c>ShowInTaskbar = false</c> with <c>Topmost</c>, so it behaves like an indicator
/// rather than an application.</item>
/// </list>
/// <para>
/// Engine state is <b>polled</b> on a dispatcher timer rather than pushed. The engine raises
/// <c>Changed</c> on a background thread at buffer rate, and marshalling every one of those to
/// the UI thread would be far more traffic than a display refresh needs — the same pattern the
/// previous window used, and the same reason.
/// </para>
/// </remarks>
public sealed class FlowBarWindow : Window
{
    private readonly Composition? _composition;
    private readonly AppSettings? _settings;

    private readonly Border _pill;
    private readonly StatusDot _dot;
    private readonly WaveBars _wave;
    private readonly PulseDots _pulse;
    private readonly TextBlock _label;
    private readonly SoftButton _cancel;
    private readonly StackPanel _content;

    private readonly DispatcherTimer _poll;

    private Point _pressOrigin;
    private bool _pressed;
    private bool _dragging;
    private BarState _state = BarState.Idle;

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
        SystemDecorations = SystemDecorations.None;
        ExtendClientAreaToDecorationsHint = false;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SizeToContent = SizeToContent.Manual;

        _dot = new StatusDot
        {
            DotColor = Tokens.Colors.Live,
            UnlitOpacity = 0.45,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _wave = new WaveBars { VerticalAlignment = VerticalAlignment.Stretch, Height = 18 };

        _pulse = new PulseDots
        {
            IsVisible = false,
            Height = PulseDots.DotSize,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _label = new TextBlock
        {
            Text = "Murmur",
            FontFamily = Tokens.Fonts.Ui,
            FontSize = Tokens.Fonts.Caption,
            FontWeight = FontWeight.Medium,
            Foreground = Tokens.Brushes.BarInkMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _cancel = new SoftButton
        {
            Content = "✕",
            Tone = ButtonTone.Quiet,
            IsOnBar = true,
            IsVisible = false,
            FontSize = Tokens.Fonts.Caption,
            Padding = new Thickness(Tokens.Space.Snug, Tokens.Space.Hair),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _cancel.Click += (_, _) => Toggle();

        _content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _dot, _wave, _pulse, _label, _cancel },
        };

        _pill = new Border
        {
            Background = Tokens.Brushes.BarBody,
            CornerRadius = new CornerRadius(Tokens.Radius.Pill),
            BorderBrush = new SolidColorBrush(Tokens.Colors.BarEdge),
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

        ApplyState(_composition is not null && !Composition.IsModelInstalled
            ? BarState.Unavailable
            : BarState.Idle);

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = Tokens.Motion.Poll };
        _poll.Tick += (_, _) => SyncFromEngine();
        _poll.Start();

        if (_composition?.Engine is not null)
        {
            _composition.Engine.CaptureFaulted += OnCaptureFaulted;
            _composition.Engine.Start();
        }
    }

    /// <summary>Which edge the bar is docked to.</summary>
    public ScreenEdge Edge { get; private set; }

    /// <summary>How far along that edge, 0…1.</summary>
    public double Offset { get; private set; }

    /// <summary>What the bar is showing. Exposed for headless tests.</summary>
    public BarState State => _state;

    /// <summary>The level display. Exposed for headless tests.</summary>
    public WaveBars Wave => _wave;

    /// <summary>The state dot. Exposed for headless tests.</summary>
    public StatusDot Dot => _dot;

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
    public void ApplyState(BarState state)
    {
        _state = state;

        var listening = state == BarState.Listening;
        var processing = state == BarState.Processing;

        // Bars for the two states where a level means something; pulse dots while transcribing;
        // neither when there is no model, because there is nothing to show a level of.
        _wave.IsActive = listening;
        _wave.IsVisible = state is BarState.Idle or BarState.Listening;
        _pulse.IsVisible = processing;
        _cancel.IsVisible = listening;

        _dot.IsLit = listening;
        _dot.DotColor = state switch
        {
            BarState.Listening => Tokens.Colors.Live,
            BarState.Processing => Tokens.Colors.Accent,
            BarState.Unavailable => Tokens.Colors.Notice,
            _ => Tokens.Colors.BarWaveIdle,
        };

        // Unavailable keeps its dot visible: "nothing will happen if you press the key" has to
        // be legible from the bar, because on Windows there is no fallback engine.
        if (state == BarState.Unavailable) _dot.IsLit = true;

        // Only the states that need explaining carry a word. At rest the shape is the label.
        _label.Text = state switch
        {
            BarState.Processing => "Transcribing",
            BarState.Unavailable => "No model",
            _ => string.Empty,
        };
        _label.IsVisible = !string.IsNullOrEmpty(_label.Text);

        _pill.Background = new SolidColorBrush(
            listening ? Tokens.Colors.BarBodyActive : Tokens.Colors.BarBody);

        Resize();
    }

    /// <summary>The pill's length along its long axis, for the current state.</summary>
    public double PillLength => _state switch
    {
        BarState.Listening => Tokens.Bar.ListeningWidth,
        BarState.Processing => Tokens.Bar.ProcessingWidth,
        _ => Tokens.Bar.IdleWidth,
    };

    private void Resize()
    {
        var horizontal = FlowBarPlacement.IsHorizontal(Edge);

        Width = horizontal ? PillLength : Tokens.Bar.Height;
        Height = horizontal ? Tokens.Bar.Height : PillLength;

        _content.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        _wave.IsVertical = !horizontal;

        if (horizontal)
        {
            _wave.Width = double.NaN;
            _wave.Height = 18;
            _pill.Padding = new Thickness(Tokens.Bar.PaddingLong, Tokens.Bar.PaddingShort);
        }
        else
        {
            _wave.Width = 18;
            _wave.Height = double.NaN;
            _pill.Padding = new Thickness(Tokens.Bar.PaddingShort, Tokens.Bar.PaddingLong);
        }

        // A side-docked bar has no room for a word, so the label goes and the dot carries the
        // state on its own.
        _label.IsVisible = horizontal && !string.IsNullOrEmpty(_label.Text);

        MoveToDock();
    }

    // ---- Engine ----

    /// <summary>Pulls state and level from the engine onto the bar.</summary>
    private void SyncFromEngine()
    {
        var engine = _composition?.Engine;
        if (engine is null) return;

        _wave.Level = engine.Level;

        var wanted = engine.State switch
        {
            DictationState.Recording => BarState.Listening,
            DictationState.Transcribing => BarState.Processing,
            _ => Composition.IsModelInstalled ? BarState.Idle : BarState.Unavailable,
        };

        if (wanted != _state) ApplyState(wanted);
    }

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
            _label.IsVisible = FlowBarPlacement.IsHorizontal(Edge);
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

        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                hub, history, settings,
                new Separator(),
                _microphoneMenu,
                new Separator(),
                bottom, left, right,
                new Separator(),
                hide, quit,
            },
        };

        menu.Opening += (_, _) => RebuildMicrophoneMenu();
        return menu;
    }

    private MenuItem? _microphoneMenu;

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

    private static TextBlock? Tick(bool selected) => selected
        ? new TextBlock { Text = "✓", Foreground = Tokens.Brushes.Accent }
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
            Background = new SolidColorBrush(Tokens.Colors.BarBody);
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
        if (_composition?.Engine is not null) _composition.Engine.CaptureFaulted -= OnCaptureFaulted;

        _poll.Stop();

        base.OnClosed(e);
    }
}
