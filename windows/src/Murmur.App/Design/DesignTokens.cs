using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Murmur.App.Design;

/// <summary>
/// The design system, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Direction: a quiet, modern dictation utility. Two surfaces, and they are deliberately
/// different materials:
/// </para>
/// <list type="bullet">
/// <item><b>The bar</b> — the floating pill that lives above every other window — is a dark,
/// softly-lit object with white level bars. It has to read instantly against a bright document,
/// a dark editor or a video call, and dark-on-anything is the only value that always does.</item>
/// <item><b>The hub</b> — the management window — is light, neutral and mostly white space.
/// It is a place you visit occasionally to read your own words back.</item>
/// </list>
/// <para>
/// <b>Views must not contain literal values.</b> If a control needs a number that isn't here,
/// add the token rather than inlining it.
/// </para>
/// <para>Three rules that are not negotiable:</para>
/// <list type="bullet">
/// <item><b>The bar is dark in both themes.</b> It is an object floating over other people's
/// applications, not a panel inside ours, so it does not follow the system appearance.</item>
/// <item><b>One accent colour, used sparingly.</b> Selection, focus and the live indicator.
/// Nothing else is coloured.</item>
/// <item><b>No skeuomorphism.</b> No bevels, no drawn grain, no fake screws, no needles.
/// Depth comes from a single soft shadow and one hairline border.</item>
/// </list>
/// <para>
/// This replaces the previous "vintage tape deck" language wholesale. It is deliberately one
/// system rather than two: a token file with both palettes in it is how a redesign quietly
/// becomes a hybrid.
/// </para>
/// </remarks>
public static class Tokens
{
    /// <summary>Whether the hub is showing its dark palette.</summary>
    /// <remarks>
    /// Only the hub and its dialogs follow this. The floating bar is dark either way.
    /// </remarks>
    public static bool IsDark =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    // ---- Colour ----

    /// <summary>Surfaces, from the window backdrop inward.</summary>
    public static class Colors
    {
        /// <summary>The hub window's backdrop.</summary>
        public static Color Canvas => Theme(0xFAFAF8, 0x161618);

        /// <summary>Cards, rows and fields sitting on the canvas.</summary>
        public static Color Surface => Theme(0xFFFFFF, 0x1F1F22);

        /// <summary>A quieter surface — the sidebar, section headers, empty states.</summary>
        public static Color SurfaceMuted => Theme(0xF3F3F0, 0x1A1A1D);

        /// <summary>Row under the pointer.</summary>
        public static Color Hover => Theme(0xEFEFEB, 0x26262A);

        /// <summary>The selected navigation item.</summary>
        public static Color Selected => Theme(0xE9E9FB, 0x2A2A3C);

        /// <summary>The one hairline that separates anything from anything.</summary>
        public static Color Line => Theme(0xE6E5E0, 0x2C2C31);

        /// <summary>Primary text.</summary>
        public static Color Ink => Theme(0x1A1A18, 0xF2F2EF);

        /// <summary>Supporting text — timestamps, counts, captions.</summary>
        public static Color InkMuted => Theme(0x6B6A64, 0x9C9C97);

        /// <summary>Placeholder and disabled text.</summary>
        public static Color InkFaint => Theme(0x9C9A94, 0x6A6A68);

        /// <summary>
        /// The accent. Selection, focus, the live indicator, the primary button.
        /// </summary>
        /// <remarks>
        /// One colour, and only in those four places. A second accent is how a minimal
        /// interface stops being one.
        /// </remarks>
        public static Color Accent => Theme(0x5B5BD6, 0x8A8AF0);

        /// <summary>A wash of the accent, for the selected navigation row.</summary>
        public static Color AccentSoft => Theme(0xEDEDFC, 0x25253A);

        /// <summary>Text and glyphs sitting on the accent.</summary>
        public static Color OnAccent => Rgb(0xFFFFFF);

        /// <summary>Attention without alarm — the missing-model notice.</summary>
        public static Color Notice => Theme(0xB4791F, 0xE0A94A);

        /// <summary>A wash of <see cref="Notice"/>.</summary>
        public static Color NoticeSoft => Theme(0xFBF3E2, 0x2E2618);

        /// <summary>Destructive actions, and the live-recording dot.</summary>
        public static Color Live => Theme(0xD3453B, 0xE8635A);

        // ---- The floating bar. Fixed values: it is dark in both themes. ----

        /// <summary>The bar's body.</summary>
        public static Color BarBody => Rgb(0x1B1B1F);

        /// <summary>The bar's body while listening — a shade lighter, so the state reads.</summary>
        public static Color BarBodyActive => Rgb(0x232329);

        /// <summary>The bar's hairline edge, which is what keeps it from vanishing on black.</summary>
        public static Color BarEdge => Rgb(0x3A3A42);

        /// <summary>Text on the bar.</summary>
        public static Color BarInk => Rgb(0xEDEDF2);

        /// <summary>Secondary text on the bar.</summary>
        public static Color BarInkMuted => Rgb(0x8E8E9A);

        /// <summary>The level bars. White, which is the whole visual signature of the thing.</summary>
        public static Color BarWave => Rgb(0xFFFFFF);

        /// <summary>The level bars at rest — present, but clearly not listening.</summary>
        public static Color BarWaveIdle => Rgb(0x6E6E7A);

        private static Color Rgb(uint hex) => Color.FromRgb(
            (byte)((hex >> 16) & 0xFF), (byte)((hex >> 8) & 0xFF), (byte)(hex & 0xFF));

        private static Color Theme(uint light, uint dark) => Rgb(IsDark ? dark : light);
    }

    /// <summary>Brushes for the colours above, allocated per call.</summary>
    public static class Brushes
    {
        /// <inheritdoc cref="Colors.Canvas"/>
        public static IBrush Canvas => new SolidColorBrush(Colors.Canvas);

        /// <inheritdoc cref="Colors.Surface"/>
        public static IBrush Surface => new SolidColorBrush(Colors.Surface);

        /// <inheritdoc cref="Colors.SurfaceMuted"/>
        public static IBrush SurfaceMuted => new SolidColorBrush(Colors.SurfaceMuted);

        /// <inheritdoc cref="Colors.Line"/>
        public static IBrush Line => new SolidColorBrush(Colors.Line);

        /// <inheritdoc cref="Colors.Ink"/>
        public static IBrush Ink => new SolidColorBrush(Colors.Ink);

        /// <inheritdoc cref="Colors.InkMuted"/>
        public static IBrush InkMuted => new SolidColorBrush(Colors.InkMuted);

        /// <inheritdoc cref="Colors.InkFaint"/>
        public static IBrush InkFaint => new SolidColorBrush(Colors.InkFaint);

        /// <inheritdoc cref="Colors.Accent"/>
        public static IBrush Accent => new SolidColorBrush(Colors.Accent);

        /// <inheritdoc cref="Colors.AccentSoft"/>
        public static IBrush AccentSoft => new SolidColorBrush(Colors.AccentSoft);

        /// <inheritdoc cref="Colors.OnAccent"/>
        public static IBrush OnAccent => new SolidColorBrush(Colors.OnAccent);

        /// <inheritdoc cref="Colors.Notice"/>
        public static IBrush Notice => new SolidColorBrush(Colors.Notice);

        /// <inheritdoc cref="Colors.NoticeSoft"/>
        public static IBrush NoticeSoft => new SolidColorBrush(Colors.NoticeSoft);

        /// <inheritdoc cref="Colors.Live"/>
        public static IBrush Live => new SolidColorBrush(Colors.Live);

        /// <inheritdoc cref="Colors.BarBody"/>
        public static IBrush BarBody => new SolidColorBrush(Colors.BarBody);

        /// <inheritdoc cref="Colors.BarInk"/>
        public static IBrush BarInk => new SolidColorBrush(Colors.BarInk);

        /// <inheritdoc cref="Colors.BarInkMuted"/>
        public static IBrush BarInkMuted => new SolidColorBrush(Colors.BarInkMuted);
    }

    // ---- Type ----

    /// <summary>
    /// One family, five sizes.
    /// </summary>
    /// <remarks>
    /// Inter is embedded in the app (<c>WithInterFont</c>), so it renders identically on a
    /// clean Windows install and on a headless CI runner — which is what makes the rendered
    /// screenshots in <c>artifacts/ui-preview</c> worth looking at.
    /// </remarks>
    public static class Fonts
    {
        /// <summary>The interface typeface.</summary>
        public static FontFamily Ui { get; } =
            new("Inter, Segoe UI, -apple-system, Helvetica Neue, sans-serif");

        /// <summary>Timings and counters. Monospaced so digits don't shift as they tick.</summary>
        public static FontFamily Mono { get; } =
            new("Cascadia Mono, Consolas, SF Mono, Menlo, monospace");

        /// <summary>Micro-labels: counts, timestamps, chips.</summary>
        public const double Caption = 11;

        /// <summary>Secondary text and section headers.</summary>
        public const double Label = 12;

        /// <summary>Body text — transcripts, dictionary entries, settings copy.</summary>
        public const double Body = 13.5;

        /// <summary>Emphasised body — a navigation item, a card title.</summary>
        public const double Subtitle = 15;

        /// <summary>Section titles.</summary>
        public const double Title = 21;

        /// <summary>A single statistic, standing alone.</summary>
        public const double Display = 30;

        /// <summary>Tracking for small uppercase headers, in device-independent pixels.</summary>
        public const double HeaderTracking = 0.6;

        /// <summary>Line height multiplier for wrapped body copy.</summary>
        public const double BodyLineHeight = 1.45;
    }

    // ---- Geometry ----

    /// <summary>A 4pt grid. Nothing sits between steps.</summary>
    public static class Space
    {
        /// <summary>2</summary>
        public const double Hair = 2;

        /// <summary>4</summary>
        public const double Tight = 4;

        /// <summary>8</summary>
        public const double Snug = 8;

        /// <summary>12</summary>
        public const double Base = 12;

        /// <summary>16</summary>
        public const double Roomy = 16;

        /// <summary>24</summary>
        public const double Wide = 24;

        /// <summary>32</summary>
        public const double Section = 32;

        /// <summary>48</summary>
        public const double Page = 48;
    }

    /// <summary>
    /// Generous by design. Soft edges are what separate a piece of software from a machine.
    /// </summary>
    public static class Radius
    {
        /// <summary>Dividers — square.</summary>
        public const double None = 0;

        /// <summary>Chips and badges.</summary>
        public const double Chip = 6;

        /// <summary>Buttons, fields, navigation rows.</summary>
        public const double Control = 9;

        /// <summary>Cards and grouped panels.</summary>
        public const double Card = 13;

        /// <summary>The hub window.</summary>
        public const double Window = 14;

        /// <summary>
        /// The floating bar. Large enough that the shape is read as a pill at any size.
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

    // ---- The floating bar ----

    /// <summary>
    /// The dimensions and behaviour of the pill that floats above every other window.
    /// </summary>
    /// <remarks>
    /// The bar changes size with state — idle is unobtrusive, listening makes room for the
    /// level bars and a cancel target — so the window is resized and re-docked on every state
    /// change rather than reserving the largest footprint and leaving transparent dead space
    /// that would swallow clicks meant for the window underneath.
    /// </remarks>
    public static class Bar
    {
        /// <summary>Height of the horizontal pill.</summary>
        public const double Height = 40;

        /// <summary>
        /// Width at rest.
        /// </summary>
        /// <remarks>
        /// Sized to the level bars and the state dot, and nothing else. The wordmark is dropped
        /// at rest deliberately: idle is the state the bar spends its life in, and a widget
        /// that sits over other people's work all day should take up as little of it as it can.
        /// </remarks>
        public const double IdleWidth = 124;

        /// <summary>Width while listening — room for the level bars and a cancel target.</summary>
        public const double ListeningWidth = 180;

        /// <summary>Width while the utterance is being transcribed, with a word to explain it.</summary>
        public const double ProcessingWidth = 168;

        /// <summary>Inner padding along the long axis.</summary>
        public const double PaddingLong = 14;

        /// <summary>Inner padding across the short axis.</summary>
        public const double PaddingShort = 6;

        /// <summary>How far the drop shadow reaches. The bar's only depth cue.</summary>
        public const double ShadowBlur = 22;

        /// <summary>How far the shadow is offset downward.</summary>
        public const double ShadowOffset = 4;

        /// <summary>Shadow opacity.</summary>
        public const double ShadowOpacity = 0.28;

        /// <summary>Diameter of the state dot at the leading end of the bar.</summary>
        public const double DotSize = 8;
    }

    /// <summary>
    /// The level display: a row of rounded bars that rise and fall with the voice.
    /// </summary>
    /// <remarks>
    /// This replaces the VU needle outright. A needle is an instrument; these are the "moving
    /// white bars" a modern dictation bar shows, and they read at 20 pixels tall where a
    /// needle does not.
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

        /// <summary>The bar changing state or size.</summary>
        public static TimeSpan State { get; } = TimeSpan.FromMilliseconds(160);

        /// <summary>Section changes in the hub.</summary>
        public static TimeSpan Panel { get; } = TimeSpan.FromMilliseconds(200);

        /// <summary>Animation frame interval — 60fps.</summary>
        public static TimeSpan Frame { get; } = TimeSpan.FromMilliseconds(16);

        /// <summary>How often the UI samples engine state. Far below frame rate on purpose.</summary>
        public static TimeSpan Poll { get; } = TimeSpan.FromMilliseconds(60);

        /// <summary>
        /// How long after the last drag movement the bar snaps to an edge.
        /// </summary>
        /// <remarks>
        /// A native move-drag takes over the mouse, so there is no pointer-release event to
        /// hang the snap on. Settling after a pause is what is left.
        /// </remarks>
        public static TimeSpan SnapDelay { get; } = TimeSpan.FromMilliseconds(220);

        /// <summary>Period of the processing pulse.</summary>
        public static TimeSpan Pulse { get; } = TimeSpan.FromMilliseconds(900);
    }
}
