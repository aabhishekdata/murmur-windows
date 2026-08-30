using Murmur.Core;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>
/// Where the floating bar lands when you let go of it.
/// </summary>
/// <remarks>
/// This is arithmetic that decides where a frameless, always-on-top, taskbar-less window
/// appears. Get it wrong and the bar is off-screen with no way to drag it back, so it is
/// tested here rather than discovered on the user's desk.
/// </remarks>
public sealed class FlowBarPlacementTests
{
    private static readonly PlacementRect Screen = new(0, 0, 1920, 1040);

    private static PlacementRect Bar(double x, double y, double w = 220, double h = 44) => new(x, y, w, h);

    [Fact]
    public void A_bar_near_the_bottom_docks_to_the_bottom()
    {
        FlowBarPlacement.NearestEdge(Screen, Bar(900, 980)).ShouldBe(ScreenEdge.Bottom);
    }

    [Fact]
    public void A_bar_dragged_hard_left_docks_to_the_left()
    {
        FlowBarPlacement.NearestEdge(Screen, Bar(4, 400)).ShouldBe(ScreenEdge.Left);
    }

    [Fact]
    public void A_bar_dragged_hard_right_docks_to_the_right()
    {
        FlowBarPlacement.NearestEdge(Screen, Bar(1920 - 224, 400)).ShouldBe(ScreenEdge.Right);
    }

    [Fact]
    public void The_bottom_edge_wins_a_close_call()
    {
        // The bottom is the resting place. Nudging the bar towards a corner must not flip it
        // vertical, or the bar reorients every time the user moves it slightly.
        var nearBottomLeftCorner = Bar(60, 940);

        FlowBarPlacement.NearestEdge(Screen, nearBottomLeftCorner).ShouldBe(ScreenEdge.Bottom);
    }

    [Fact]
    public void The_default_placement_is_bottom_centre()
    {
        var (x, y) = FlowBarPlacement.Position(
            Screen, 220, 44, ScreenEdge.Bottom, FlowBarPlacement.DefaultOffset);

        x.ShouldBe((1920 - 220) / 2.0, tolerance: 0.001);
        y.ShouldBe(1040 - 44 - FlowBarPlacement.EdgeMargin);
    }

    [Fact]
    public void A_docked_bar_never_leaves_the_work_area()
    {
        foreach (var edge in new[] { ScreenEdge.Bottom, ScreenEdge.Left, ScreenEdge.Right })
        {
            foreach (var offset in new[] { -5.0, 0.0, 0.5, 1.0, 7.0 })
            {
                var (x, y) = FlowBarPlacement.Position(Screen, 220, 44, edge, offset);

                x.ShouldBeGreaterThanOrEqualTo(Screen.X);
                y.ShouldBeGreaterThanOrEqualTo(Screen.Y);
                (x + 220).ShouldBeLessThanOrEqualTo(Screen.Right);
                (y + 44).ShouldBeLessThanOrEqualTo(Screen.Bottom);
            }
        }
    }

    [Fact]
    public void A_bar_wider_than_the_screen_still_lands_inside_it()
    {
        // A tiny work area (a remote session, a scaled-up display) must not produce a negative
        // position that puts the window off the top-left of the desktop.
        var tiny = new PlacementRect(0, 0, 120, 90);
        var (x, y) = FlowBarPlacement.Position(tiny, 220, 44, ScreenEdge.Bottom, 0.5);

        x.ShouldBe(0);
        y.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Position_and_offset_are_inverses()
    {
        // Drag, save the fraction, relaunch, restore: the bar must come back where it was.
        foreach (var edge in new[] { ScreenEdge.Bottom, ScreenEdge.Left, ScreenEdge.Right })
        {
            foreach (var offset in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                var (x, y) = FlowBarPlacement.Position(Screen, 220, 44, edge, offset);
                var round = FlowBarPlacement.OffsetFor(Screen, Bar(x, y), edge);

                round.ShouldBe(offset, 0.001, $"{edge} at {offset} did not round-trip");
            }
        }
    }

    [Fact]
    public void A_saved_fraction_survives_a_change_of_monitor()
    {
        // The reason position is stored as a fraction: the same setting has to be sane on a
        // laptop panel after being saved on a 4K display.
        var laptop = new PlacementRect(0, 0, 1280, 720);
        var (x, y) = FlowBarPlacement.Position(laptop, 220, 44, ScreenEdge.Right, 0.9);

        (x + 220).ShouldBeLessThanOrEqualTo(laptop.Right);
        (y + 44).ShouldBeLessThanOrEqualTo(laptop.Bottom);
    }

    [Fact]
    public void The_bar_is_horizontal_only_along_the_bottom()
    {
        FlowBarPlacement.IsHorizontal(ScreenEdge.Bottom).ShouldBeTrue();
        FlowBarPlacement.IsHorizontal(ScreenEdge.Left).ShouldBeFalse();
        FlowBarPlacement.IsHorizontal(ScreenEdge.Right).ShouldBeFalse();
    }

    [Fact]
    public void A_work_area_offset_by_a_second_monitor_is_respected()
    {
        // Screens to the left of the primary have negative coordinates on Windows.
        var secondary = new PlacementRect(-1920, -120, 1920, 1080);
        var (x, y) = FlowBarPlacement.Position(secondary, 220, 44, ScreenEdge.Bottom, 0.5);

        x.ShouldBeInRange(secondary.X, secondary.Right - 220);
        y.ShouldBeInRange(secondary.Y, secondary.Bottom - 44);
    }
}

/// <summary>The home-screen figures.</summary>
public sealed class TranscriptStatsTests
{
    private static readonly DateOnly Today = new(2026, 8, 29);

    /// <summary>
    /// A record stamped at midday <i>local</i> time on the given day.
    /// </summary>
    /// <remarks>
    /// Local, not UTC: the stats group by local calendar day, so a UTC-stamped fixture would
    /// land on the wrong day — and pass or fail depending on the runner's time zone.
    /// </remarks>
    private static TranscriptRecord At(int daysAgo, string text = "hello there", double seconds = 2) =>
        new()
        {
            At = new DateTimeOffset(DateTime.SpecifyKind(
                Today.AddDays(-daysAgo).ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Local)),
            Text = text,
            AudioSeconds = seconds,
        };

    [Fact]
    public void An_empty_history_summarises_to_zeroes()
    {
        var stats = TranscriptStats.Summarise([], Today);

        stats.Recordings.ShouldBe(0);
        stats.Words.ShouldBe(0);
        stats.WordsPerMinute.ShouldBe(0);
        stats.StreakDays.ShouldBe(0);
    }

    [Fact]
    public void Words_are_counted_across_every_record()
    {
        var stats = TranscriptStats.Summarise([At(0, "one two three"), At(0, "four five")], Today);

        stats.Recordings.ShouldBe(2);
        stats.Words.ShouldBe(5);
    }

    [Fact]
    public void Speaking_rate_is_words_over_audio_time()
    {
        // 6 words in 4 seconds is 90 wpm.
        var stats = TranscriptStats.Summarise(
            [At(0, "a b c d e f", seconds: 4)], Today);

        stats.WordsPerMinute.ShouldBe(90);
    }

    [Fact]
    public void A_history_of_keypress_length_clips_does_not_divide_by_zero()
    {
        var stats = TranscriptStats.Summarise([At(0, "hi", seconds: 0)], Today);

        stats.WordsPerMinute.ShouldBe(0);
    }

    [Fact]
    public void Consecutive_days_make_a_streak()
    {
        TranscriptStats.Streak([At(0), At(1), At(2)], Today).ShouldBe(3);
    }

    [Fact]
    public void A_gap_ends_the_streak()
    {
        TranscriptStats.Streak([At(0), At(1), At(4), At(5)], Today).ShouldBe(2);
    }

    [Fact]
    public void Yesterday_still_counts_as_a_live_streak()
    {
        // It is 8am and the user has not dictated yet. Zeroing their streak at midnight would
        // read as a bug.
        TranscriptStats.Streak([At(1), At(2)], Today).ShouldBe(2);
    }

    [Fact]
    public void A_streak_that_stopped_days_ago_is_not_a_streak()
    {
        TranscriptStats.Streak([At(3), At(4), At(5)], Today).ShouldBe(0);
    }

    [Fact]
    public void Several_dictations_on_one_day_are_still_one_day()
    {
        TranscriptStats.Streak([At(0), At(0), At(0)], Today).ShouldBe(1);
    }

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(1, "Yesterday")]
    [InlineData(5, "This week")]
    [InlineData(20, "This month")]
    [InlineData(400, "Earlier")]
    public void History_is_grouped_chronologically(int daysAgo, string expected)
    {
        TranscriptStats.GroupFor(At(daysAgo).At, Today).ShouldBe(expected);
    }
}
