namespace Murmur.Core;

/// <summary>Which screen edge the floating bar is docked to.</summary>
/// <remarks>
/// Bottom, left and right only. A bar pinned under the title bars along the top edge is in the
/// way of every application's own chrome, and it is not an edge the reference design offers.
/// </remarks>
public enum ScreenEdge
{
    /// <summary>Along the bottom of the work area. The default, and horizontal.</summary>
    Bottom,

    /// <summary>Down the left of the work area. The bar reorients vertically.</summary>
    Left,

    /// <summary>Down the right of the work area. The bar reorients vertically.</summary>
    Right,
}

/// <summary>A rectangle in screen coordinates, free of any UI framework type.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct PlacementRect(double X, double Y, double Width, double Height)
{
    /// <summary>The right edge.</summary>
    public double Right => X + Width;

    /// <summary>The bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>Horizontal centre.</summary>
    public double CentreX => X + (Width / 2);

    /// <summary>Vertical centre.</summary>
    public double CentreY => Y + (Height / 2);
}

/// <summary>
/// Where the floating bar sits, and where it snaps to when you let go of it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately free of Avalonia: this is arithmetic, and arithmetic that decides where a
/// window lands on a user's screen is worth unit-testing on a machine that has no screen. The
/// window layer converts to and from <c>PixelPoint</c> and does nothing else.
/// </para>
/// <para>
/// Position is persisted as an <b>edge plus a fraction</b> rather than as pixels. A saved
/// absolute position is wrong the moment the user unplugs a monitor or changes scaling — the
/// bar reappears off-screen, which for a frameless always-on-top window means it is simply
/// gone. A fraction of the free space along an edge survives all of that.
/// </para>
/// </remarks>
public static class FlowBarPlacement
{
    /// <summary>Gap between the bar and the screen edge it is docked to.</summary>
    public const double EdgeMargin = 18;

    /// <summary>
    /// Bias, in device-independent pixels, applied to the bottom edge when choosing which edge
    /// is nearest.
    /// </summary>
    /// <remarks>
    /// The bottom is the resting place — it is where the bar starts and where it belongs — so a
    /// drag has to clearly favour a side before the bar reorients itself vertically.
    /// </remarks>
    public const double BottomPreference = 120;

    /// <summary>The default placement for a fresh install: bottom edge, centred.</summary>
    public const double DefaultOffset = 0.5;

    /// <summary>
    /// Picks the edge a bar at <paramref name="bar"/> should snap to.
    /// </summary>
    /// <param name="workArea">The usable screen area, excluding the taskbar.</param>
    /// <param name="bar">The bar's current position and size.</param>
    /// <returns>The nearest edge, with the bottom favoured.</returns>
    public static ScreenEdge NearestEdge(PlacementRect workArea, PlacementRect bar)
    {
        var toBottom = Math.Abs(workArea.Bottom - bar.Bottom) - BottomPreference;
        var toLeft = Math.Abs(bar.X - workArea.X);
        var toRight = Math.Abs(workArea.Right - bar.Right);

        if (toBottom <= toLeft && toBottom <= toRight) return ScreenEdge.Bottom;
        return toLeft <= toRight ? ScreenEdge.Left : ScreenEdge.Right;
    }

    /// <summary>
    /// How far along <paramref name="edge"/> a bar at <paramref name="bar"/> currently sits.
    /// </summary>
    /// <param name="workArea">The usable screen area.</param>
    /// <param name="bar">The bar's current position and size.</param>
    /// <param name="edge">The edge it is being docked to.</param>
    /// <returns>0 at the start of the edge, 1 at the end, 0.5 centred.</returns>
    public static double OffsetFor(PlacementRect workArea, PlacementRect bar, ScreenEdge edge)
    {
        if (edge == ScreenEdge.Bottom)
        {
            var free = workArea.Width - bar.Width - (EdgeMargin * 2);
            return free <= 0 ? DefaultOffset : Clamp01((bar.X - workArea.X - EdgeMargin) / free);
        }

        var freeVertical = workArea.Height - bar.Height - (EdgeMargin * 2);
        return freeVertical <= 0 ? DefaultOffset : Clamp01((bar.Y - workArea.Y - EdgeMargin) / freeVertical);
    }

    /// <summary>
    /// The top-left corner a bar of the given size takes when docked.
    /// </summary>
    /// <param name="workArea">The usable screen area.</param>
    /// <param name="width">Bar width, already reoriented for the edge.</param>
    /// <param name="height">Bar height, already reoriented for the edge.</param>
    /// <param name="edge">Which edge to dock to.</param>
    /// <param name="offset">How far along the edge, 0…1.</param>
    /// <returns>The bar's position, always fully inside the work area.</returns>
    public static (double X, double Y) Position(
        PlacementRect workArea, double width, double height, ScreenEdge edge, double offset)
    {
        var slide = Clamp01(offset);

        if (edge == ScreenEdge.Bottom)
        {
            var free = Math.Max(0, workArea.Width - width - (EdgeMargin * 2));
            var x = workArea.X + EdgeMargin + (free * slide);
            var y = workArea.Bottom - height - EdgeMargin;
            return (Clamp(x, workArea.X, workArea.Right - width), Clamp(y, workArea.Y, workArea.Bottom - height));
        }

        var freeVertical = Math.Max(0, workArea.Height - height - (EdgeMargin * 2));
        var top = workArea.Y + EdgeMargin + (freeVertical * slide);

        var left = edge == ScreenEdge.Left
            ? workArea.X + EdgeMargin
            : workArea.Right - width - EdgeMargin;

        return (Clamp(left, workArea.X, workArea.Right - width),
                Clamp(top, workArea.Y, workArea.Bottom - height));
    }

    /// <summary>Whether the bar is horizontal on this edge.</summary>
    /// <param name="edge">The edge in question.</param>
    /// <returns>True along the bottom; false down either side.</returns>
    public static bool IsHorizontal(ScreenEdge edge) => edge == ScreenEdge.Bottom;

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private static double Clamp(double value, double low, double high) =>
        high <= low ? low : Math.Clamp(value, low, high);
}
