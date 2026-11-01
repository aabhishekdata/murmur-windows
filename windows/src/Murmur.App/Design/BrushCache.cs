using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Murmur.App.Design;

/// <summary>
/// One immutable brush per colour and opacity, shared by everything that paints with it.
/// </summary>
/// <remarks>
/// <para>
/// Every token brush and every custom control's <c>Render</c> used to allocate a fresh
/// <see cref="SolidColorBrush"/> on each read — the level bars alone made thirteen of them per
/// frame. A mutable brush is also a property-system object the renderer has to watch for
/// changes; an <see cref="ImmutableSolidColorBrush"/> is a plain value it can simply use.
/// </para>
/// <para>
/// Keyed by the resolved colour, so the light and dark variants of a token are naturally two
/// entries. The cache is emptied when the application's theme variant changes, which keeps it
/// to the brushes of the theme actually showing; a brush handed out before the switch stays
/// valid, it just stops being shared.
/// </para>
/// <para>
/// Opacity is quantised to 1/<see cref="OpacitySteps"/>, which no eye can tell apart and which
/// keeps an animated opacity (the processing pulse) from growing the cache without bound.
/// </para>
/// </remarks>
public static class BrushCache
{
    /// <summary>How finely opacity is distinguished.</summary>
    public const int OpacitySteps = 64;

    private static readonly ConcurrentDictionary<(uint Argb, int Opacity), IImmutableSolidColorBrush> s_brushes = new();

    private static Application? s_watched;

    /// <summary>How many brushes are cached. Exposed for tests.</summary>
    public static int Count => s_brushes.Count;

    /// <summary>The shared brush for <paramref name="colour"/> at <paramref name="opacity"/>.</summary>
    /// <param name="colour">The colour, already resolved for the current theme.</param>
    /// <param name="opacity">0…1; quantised.</param>
    /// <returns>A brush that is safe to share and must not be modified.</returns>
    public static IImmutableSolidColorBrush Get(Color colour, double opacity = 1)
    {
        Watch();

        var steps = (int)Math.Round(Math.Clamp(opacity, 0, 1) * OpacitySteps);

        return s_brushes.GetOrAdd(
            (colour.ToUInt32(), steps),
            static key => new ImmutableSolidColorBrush(
                Color.FromUInt32(key.Argb), key.Opacity / (double)OpacitySteps));
    }

    /// <summary>Drops every cached brush.</summary>
    public static void Clear() => s_brushes.Clear();

    /// <summary>Subscribes to the running application's theme changes, once per application.</summary>
    private static void Watch()
    {
        var app = Application.Current;
        if (app is null || ReferenceEquals(app, s_watched)) return;

        s_watched = app;
        app.ActualThemeVariantChanged += (_, _) => Clear();
    }
}
