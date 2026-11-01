using Avalonia.Data;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Murmur.App.Design;

/// <summary>
/// The design system, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Direction: <b>Windows 11 Fluent</b>. The hub is a first-party-looking Windows window — Mica
/// behind a translucent content layer, a navigation pane, Settings made of expander cards,
/// Segoe UI Variable, the user's own accent. The macOS app keeps its own field-recorder look;
/// the two share rules, not pixels.
/// </para>
/// <list type="bullet">
/// <item><b>The bar</b> — the floating capsule that lives above every other window — is a dark,
/// near-opaque object with white level bars. It has to read instantly against a bright document,
/// a dark editor or a video call, and dark-on-anything is the only value that always does.</item>
/// <item><b>The hub</b> — the management window — follows the system: light, dark or high
/// contrast, switched live (see <see cref="ThemeResources"/>).</item>
/// </list>
/// <para>
/// <b>Views must not contain literal values.</b> If a control needs a number that isn't here,
/// add the token rather than inlining it. Views bind colours through <see cref="Live"/> so a
/// theme switch repaints them; <see cref="Colors"/> and <see cref="Brushes"/> are the current
/// values, for controls that draw themselves and for tests.
/// </para>
/// <para>Rules that are not negotiable:</para>
/// <list type="bullet">
/// <item><b>Red means recording.</b> Nothing else is red — not even a red Windows accent, which
/// <see cref="AccentPolicy"/> replaces.</item>
/// <item><b>The bar is dark in every theme.</b> It is an object floating over other people's
/// applications, not a panel inside ours.</item>
/// <item><b>Materials, not gradients.</b> Depth is Mica or acrylic from the system, a hairline,
/// and one soft shadow on the bar. There are no gradients anywhere.</item>
/// <item><b>Motion only when something changes</b>: on the bar, opacity fades of 150 ms or less
/// and the level bars while recording. Nothing loops.</item>
/// </list>
/// </remarks>
public static class Tokens
{
    /// <summary>Whether the hub is showing a dark palette (dark, or high contrast).</summary>
    /// <remarks>
    /// Only the hub and its dialogs follow this. The floating bar is dark either way.
    /// </remarks>
    public static bool IsDark => ThemeResources.CurrentKind != ThemeKind.Light;

    // ---- Colour ----

    /// <summary>
    /// The current value of each colour, in the theme showing now.
    /// </summary>
    /// <remarks>
    /// For controls that draw themselves (and repaint when the theme changes) and for tests.
    /// A view that sets a property once should bind through <see cref="Live"/> instead, or it
    /// will keep the old palette after a theme switch.
    /// </remarks>
    public static class Colors
    {
        /// <inheritdoc cref="ColorToken.Canvas"/>
        public static Color Canvas => Themed(ColorToken.Canvas);

        /// <inheritdoc cref="ColorToken.Layer"/>
        public static Color Layer => Themed(ColorToken.Layer);

        /// <inheritdoc cref="ColorToken.Surface"/>
        public static Color Surface => Themed(ColorToken.Surface);

        /// <inheritdoc cref="ColorToken.SurfaceMuted"/>
        public static Color SurfaceMuted => Themed(ColorToken.SurfaceMuted);

        /// <inheritdoc cref="ColorToken.Hover"/>
        public static Color Hover => Themed(ColorToken.Hover);

        /// <inheritdoc cref="ColorToken.Line"/>
        public static Color Line => Themed(ColorToken.Line);

        /// <inheritdoc cref="ColorToken.Ink"/>
        public static Color Ink => Themed(ColorToken.Ink);

        /// <inheritdoc cref="ColorToken.InkMuted"/>
        public static Color InkMuted => Themed(ColorToken.InkMuted);

        /// <inheritdoc cref="ColorToken.InkFaint"/>
        public static Color InkFaint => Themed(ColorToken.InkFaint);

        /// <inheritdoc cref="ColorToken.Accent"/>
        public static Color Accent => Themed(ColorToken.Accent);

        /// <inheritdoc cref="ColorToken.AccentSoft"/>
        public static Color AccentSoft => Themed(ColorToken.AccentSoft);

        /// <inheritdoc cref="ColorToken.OnAccent"/>
        public static Color OnAccent => Themed(ColorToken.OnAccent);

        /// <inheritdoc cref="ColorToken.Notice"/>
        public static Color Notice => Themed(ColorToken.Notice);

        /// <inheritdoc cref="ColorToken.NoticeSoft"/>
        public static Color NoticeSoft => Themed(ColorToken.NoticeSoft);

        /// <inheritdoc cref="ColorToken.Live"/>
        public static Color Live => Themed(ColorToken.Live);

        // ---- The floating bar. Fixed values: it is dark in every theme. ----

        /// <summary>
        /// The bar's body: near-black at 92 % opacity, so a hint of what is underneath shows at
        /// the edge without any blur to pay for.
        /// </summary>
        public static Color BarBody => Argb(0xEB161618);

        /// <summary>The bar's body while recording — a shade lighter, so the state reads.</summary>
        public static Color BarBodyActive => Argb(0xF0202024);

        /// <summary>The bar's hairline edge, which is what keeps it from vanishing on black.</summary>
        public static Color BarEdge => Argb(0x33FFFFFF);

        /// <summary>Text on the bar.</summary>
        public static Color BarInk => Argb(0xFFEDEDF2);

        /// <summary>Secondary text on the bar. Neutral on purpose.</summary>
        public static Color BarInkMuted => Argb(0xFF9A9AA6);

        /// <summary>The level bars. White, which is the whole visual signature of the thing.</summary>
        public static Color BarWave => Argb(0xFFFFFFFF);

        /// <summary>The level bars as they fall back to rest after recording.</summary>
        public static Color BarWaveIdle => Argb(0xFF6E6E7A);

        /// <summary>The state dot at rest: present, clearly not listening.</summary>
        public static Color BarDotIdle => Argb(0xFF6A6A74);

        /// <summary>The recording dot. The bar's own red, the same in every theme.</summary>
        public static Color BarLive => Argb(0xFFFF5A4F);

        /// <summary>The "can't dictate" dot. Amber, not red: it is not recording.</summary>
        public static Color BarNotice => Argb(0xFFF2B33D);

        private static Color Argb(uint value) => Color.FromUInt32(value);

        private static Color Themed(ColorToken token) => ThemeResources.Resolve(token, ThemeResources.CurrentKind);
    }

    /// <summary>
    /// Bindings to each colour that follow the theme and the accent while the view is open.
    /// </summary>
    /// <remarks>
    /// Assign in an initializer with Avalonia's binding indexer —
    /// <c>[!TextBlock.ForegroundProperty] = Tokens.Live.Ink</c> — or with
    /// <c>control.Bind(property, Tokens.Live.Ink)</c>. Each read is a new binding, so one can be
    /// used per property.
    /// </remarks>
    public static class Live
    {
        /// <inheritdoc cref="ColorToken.Canvas"/>
        public static BindingBase Canvas => For(ColorToken.Canvas);

        /// <inheritdoc cref="ColorToken.Layer"/>
        public static BindingBase Layer => For(ColorToken.Layer);

        /// <inheritdoc cref="ColorToken.Surface"/>
        public static BindingBase Surface => For(ColorToken.Surface);

        /// <inheritdoc cref="ColorToken.SurfaceMuted"/>
        public static BindingBase SurfaceMuted => For(ColorToken.SurfaceMuted);

        /// <inheritdoc cref="ColorToken.Hover"/>
        public static BindingBase Hover => For(ColorToken.Hover);

        /// <inheritdoc cref="ColorToken.Line"/>
        public static BindingBase Line => For(ColorToken.Line);

        /// <inheritdoc cref="ColorToken.Ink"/>
        public static BindingBase Ink => For(ColorToken.Ink);

        /// <inheritdoc cref="ColorToken.InkMuted"/>
        public static BindingBase InkMuted => For(ColorToken.InkMuted);

        /// <inheritdoc cref="ColorToken.InkFaint"/>
        public static BindingBase InkFaint => For(ColorToken.InkFaint);

        /// <inheritdoc cref="ColorToken.Accent"/>
        public static BindingBase Accent => For(ColorToken.Accent);

        /// <inheritdoc cref="ColorToken.AccentSoft"/>
        public static BindingBase AccentSoft => For(ColorToken.AccentSoft);

        /// <inheritdoc cref="ColorToken.OnAccent"/>
        public static BindingBase OnAccent => For(ColorToken.OnAccent);

        /// <inheritdoc cref="ColorToken.Notice"/>
        public static BindingBase Notice => For(ColorToken.Notice);

        /// <inheritdoc cref="ColorToken.NoticeSoft"/>
        public static BindingBase NoticeSoft => For(ColorToken.NoticeSoft);

        /// <summary>A binding to any token.</summary>
        /// <param name="token">The role.</param>
        /// <returns>A dynamic-resource binding.</returns>
        public static BindingBase For(ColorToken token) => new DynamicResourceExtension(ThemeResources.Key(token));
    }

    /// <summary>Brushes for the colours above, shared through <see cref="BrushCache"/>.</summary>
    /// <remarks>
    /// The current theme's brush, resolved per read: a read does not allocate, and the same token
    /// in the same theme is the same object. Not live — see <see cref="Live"/>.
    /// </remarks>
    public static class Brushes
    {
        /// <inheritdoc cref="Colors.Canvas"/>
        public static IBrush Canvas => BrushCache.Get(Colors.Canvas);

        /// <inheritdoc cref="Colors.Surface"/>
        public static IBrush Surface => BrushCache.Get(Colors.Surface);

        /// <inheritdoc cref="Colors.Line"/>
        public static IBrush Line => BrushCache.Get(Colors.Line);

        /// <inheritdoc cref="Colors.Ink"/>
        public static IBrush Ink => BrushCache.Get(Colors.Ink);

        /// <inheritdoc cref="Colors.InkMuted"/>
        public static IBrush InkMuted => BrushCache.Get(Colors.InkMuted);

        /// <inheritdoc cref="Colors.Accent"/>
        public static IBrush Accent => BrushCache.Get(Colors.Accent);

        /// <inheritdoc cref="Colors.BarBody"/>
        public static IBrush BarBody => BrushCache.Get(Colors.BarBody);

        /// <inheritdoc cref="Colors.BarInk"/>
        public static IBrush BarInk => BrushCache.Get(Colors.BarInk);

        /// <inheritdoc cref="Colors.BarInkMuted"/>
        public static IBrush BarInkMuted => BrushCache.Get(Colors.BarInkMuted);
    }

    // ---- Type ----

    /// <summary>
    /// Windows 11's type ramp, in Segoe UI Variable.
    /// </summary>
    /// <remarks>
    /// Segoe UI Variable is the Windows 11 system face; its Text optical size is drawn for 14 px
    /// and below, Display for titles. Inter is embedded (<c>WithInterFont</c>) as the fallback,
    /// which is what a headless Linux runner draws the previews with.
    /// </remarks>
    public static class Fonts
    {
        /// <summary>The interface typeface.</summary>
        public static FontFamily Ui { get; } =
            new("Segoe UI Variable Text, Segoe UI, fonts:Inter#Inter, Inter, sans-serif");

        /// <summary>Page titles and large numbers.</summary>
        public static FontFamily UiDisplay { get; } =
            new("Segoe UI Variable Display, Segoe UI, fonts:Inter#Inter, Inter, sans-serif");

        /// <summary>Timings and counters. Monospaced so digits don't shift as they tick.</summary>
        public static FontFamily Mono { get; } =
            new("Cascadia Mono, Consolas, SF Mono, Menlo, monospace");

        /// <summary>Micro-labels: counts, timestamps, chips, the bar's words.</summary>
        public const double Caption = 11;

        /// <summary>Descriptions under a setting, and secondary text.</summary>
        public const double Label = 12;

        /// <summary>Body text — transcripts, dictionary entries, setting names. Windows' 14.</summary>
        public const double Body = 14;

        /// <summary>Emphasised body — a group header, a card title, an empty state.</summary>
        public const double Subtitle = 16;

        /// <summary>A page title, as in Windows Settings.</summary>
        public const double Title = 28;

        /// <summary>A single statistic, standing alone.</summary>
        public const double Display = 32;

        /// <summary>Tracking for small headers, in device-independent pixels.</summary>
        public const double HeaderTracking = 0.2;

        /// <summary>Line height multiplier for wrapped body copy.</summary>
        public const double BodyLineHeight = 1.45;
    }

    // ---- Geometry ----

    /// <summary>A 4pt grid. Nothing sits between steps.</summary>
    public static class Space
    {
        /// <summary>2</summary>
        public const double Hair = 2;

        /// <summary>4 — also the gap between cards in a settings group.</summary>
        public const double Tight = 4;

        /// <summary>8</summary>
        public const double Snug = 8;

        /// <summary>12</summary>
        public const double Base = 12;

        /// <summary>16 — a card's inner padding.</summary>
        public const double Roomy = 16;

        /// <summary>24</summary>
        public const double Wide = 24;

        /// <summary>32 — between settings groups.</summary>
        public const double Section = 32;

        /// <summary>48</summary>
        public const double Page = 48;
    }

    /// <summary>
    /// Windows 11's corner radii: 4 on controls, 8 on cards and windows.
    /// </summary>
    public static class Radius
    {
        /// <summary>Dividers — square.</summary>
        public const double None = 0;

        /// <summary>Chips and badges.</summary>
        public const double Chip = 4;

        /// <summary>Buttons, fields, segments. Windows' ControlCornerRadius.</summary>
        public const double Control = 4;

        /// <summary>Cards and grouped panels. Windows' OverlayCornerRadius.</summary>
        public const double Card = 8;

        /// <summary>The content layer's top-left corner, and dialogs.</summary>
        public const double Window = 8;

        /// <summary>
        /// The floating bar. Large enough that the shape is read as a capsule at any size.
        /// </summary>
        public const double Pill = 999;
    }

    /// <summary>Line weights. One hairline, everywhere.</summary>
    public static class Border
    {
        /// <summary>The only border thickness in the app.</summary>
        public const double Hairline = 1;

        /// <summary>The focus ring, which has to be visible without colour.</summary>
        public const double Focus = 2;
    }

    /// <summary>The hub's frame.</summary>
    public static class Chrome
    {
        /// <summary>Height of the custom title bar, as in Windows 11's own apps.</summary>
        public const double TitleBarHeight = 32;

        /// <summary>The navigation pane, open.</summary>
        public const double PaneWidth = 240;

        /// <summary>The widest a page's content grows, so lines stay readable on a big screen.</summary>
        public const double ContentMaxWidth = 1000;

        /// <summary>Icon size in the navigation pane, cards and buttons.</summary>
        public const double Icon = 16;

        /// <summary>Icon size at the head of a settings card.</summary>
        public const double CardIcon = 20;
    }

    // ---- The floating bar ----

    /// <summary>
    /// The dimensions and behaviour of the capsule that floats above every other window.
    /// </summary>
    /// <remarks>
    /// The bar changes size with state — rest is a small capsule, recording makes room for the
    /// level bars and a cancel target — so the window is resized and re-docked on every state
    /// change rather than reserving the largest footprint and leaving transparent dead space
    /// that would swallow clicks meant for the window underneath. A state change, never a frame:
    /// nothing resizes the window while an animation runs.
    /// </remarks>
    public static class Bar
    {
        /// <summary>Height of the horizontal capsule.</summary>
        public const double Height = 36;

        /// <summary>
        /// Width at rest.
        /// </summary>
        /// <remarks>
        /// The state dot in a short capsule, and nothing else. Rest is the state the bar spends
        /// its life in, and a widget that sits over other people's work all day should take up
        /// as little of it as it can.
        /// </remarks>
        public const double IdleWidth = 72;

        /// <summary>Width while recording — room for the badge, the level bars and a cancel target.</summary>
        public const double ListeningWidth = 196;

        /// <summary>Width while the utterance is transcribed, or a reason it cannot be.</summary>
        public const double ProcessingWidth = 164;

        /// <summary>The widest a notice may make the bar; longer text is trimmed.</summary>
        public const double NoticeMaxWidth = 320;

        /// <summary>The privacy badge: laptop, cloud, or cloud-off for the offline fallback.</summary>
        public const double BadgeSize = 14;

        /// <summary>Inner padding along the long axis.</summary>
        public const double PaddingLong = 14;

        /// <summary>Inner padding across the short axis.</summary>
        public const double PaddingShort = 6;

        /// <summary>How far the drop shadow reaches. The bar's only depth cue.</summary>
        public const double ShadowBlur = 18;

        /// <summary>How far the shadow is offset downward.</summary>
        public const double ShadowOffset = 4;

        /// <summary>Shadow opacity.</summary>
        public const double ShadowOpacity = 0.32;

        /// <summary>Diameter of the state dot at the leading end of the bar.</summary>
        public const double DotSize = 8;

        /// <summary>Height of the level bars' track.</summary>
        public const double WaveHeight = 16;
    }

    /// <summary>
    /// The level display: a row of rounded bars that rise and fall with the voice.
    /// </summary>
    /// <remarks>
    /// Shown only while recording. At rest the bar shows no level at all — there is nothing to
    /// measure — which is also what lets it request no frames.
    /// </remarks>
    public static class Wave
    {
        /// <summary>How many bars are drawn.</summary>
        public const int BarCount = 13;

        /// <summary>Width of one bar.</summary>
        public const double BarWidth = 3;

        /// <summary>Gap between bars.</summary>
        public const double BarGap = 3;

        /// <summary>Shortest a bar ever gets, so the row never disappears.</summary>
        public const double MinBarLength = 3;

        /// <summary>Bar length at rest, as a fraction of the track.</summary>
        public const double IdleFraction = 0.16;

        /// <summary>
        /// How fast a bar chases the signal, 0…1 per frame.
        /// </summary>
        /// <remarks>
        /// Deliberately not 1. Bars driven straight from the RMS flicker; a little smoothing is
        /// what makes the row look like a voice rather than noise.
        /// </remarks>
        public const double Smoothing = 0.35;

        /// <summary>How fast a bar falls back when the level drops.</summary>
        public const double Decay = 0.12;

        /// <summary>Speed of the travelling wave that gives the row its motion, in cycles/second.</summary>
        public const double TravelHz = 1.6;

        /// <summary>How much of a bar's height comes from the travelling wave rather than the level.</summary>
        public const double TravelDepth = 0.5;

        /// <summary>Input gain. Speech RMS rarely exceeds ~0.3, so it is scaled up to fill the row.</summary>
        public const double Gain = 3.2;
    }

    // ---- Motion ----

    /// <summary>Quick and soft. Nothing in a utility should take longer than a glance.</summary>
    public static class Motion
    {
        /// <summary>Hover and press feedback.</summary>
        public static TimeSpan Quick { get; } = TimeSpan.FromMilliseconds(90);

        /// <summary>
        /// A fade of opacity when the bar changes state. The longest transition in the
        /// app, and the ceiling for any new one.
        /// </summary>
        public static TimeSpan Fade { get; } = TimeSpan.FromMilliseconds(140);

        /// <summary>The ceiling every transition in the app stays under.</summary>
        public static TimeSpan Ceiling { get; } = TimeSpan.FromMilliseconds(150);

        /// <summary>
        /// The physics step — 60 steps a second. Smoothing and decay are tuned per step.
        /// </summary>
        public static TimeSpan Frame { get; } = TimeSpan.FromMilliseconds(16);

        /// <summary>
        /// The shortest gap between repaints of an animation — two physics steps, so about
        /// 30 fps on a 60 Hz display.
        /// </summary>
        /// <remarks>
        /// Level bars a few pixels tall gain nothing visible from 60 fps, and every repaint of
        /// an always-on-top window is a composition the whole desktop pays for. Exactly two
        /// steps rather than 33 ms, so a 60 Hz frame clock paints every second frame instead of
        /// slipping to every third.
        /// </remarks>
        public static TimeSpan FrameCap { get; } = TimeSpan.FromMilliseconds(32);

        /// <summary>How long typing in a search box must pause before the list is filtered.</summary>
        public static TimeSpan SearchDebounce { get; } = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// How long the bar shows a notice — "Azure unreachable — used Parakeet" — before it
        /// goes back to rest. A one-shot timer, armed only while a notice is up.
        /// </summary>
        public static TimeSpan NoticeHold { get; } = TimeSpan.FromSeconds(4);

        /// <summary>
        /// How long History's Paste waits after minimising the hub, so Windows has handed focus back
        /// to the window the text is for.
        /// </summary>
        public static TimeSpan FocusReturn { get; } = TimeSpan.FromMilliseconds(250);
    }
}
