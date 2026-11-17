using Ancilla.Theater.Format;

namespace Ancilla.Theater.Tests;

public class MapTests
{
    private static readonly double[] TickMs = [0, 100, 200, 3_200, 3_300];

    // One cell per film unit, so positions map straight to cells.
    private static MapPoint Cell(FilmPosition p) => new(p.X + .5, p.Y + .5);

    private static BipedTrack Life(params (int Tick, int X, int Y)[] points) =>
        new(512, points.Select(p => (p.Tick, new FilmPosition(p.X, p.Y, 0))).ToList());

    [Fact]
    public void Time_is_held_at_each_position_until_the_next_record_and_long_gaps_are_capped()
    {
        var grid = MatchTimeline.TimeSpent([Life((0, 1, 1), (1, 1, 1), (2, 5, 2), (3, 6, 2), (4, 6, 2))], TickMs, Cell);

        Assert.Equal(200, grid[1 * MatchTimeline.MapWidth + 1]);   // ticks 0→1→2
        Assert.Equal(MatchTimeline.MaxStepMs, grid[2 * MatchTimeline.MapWidth + 5]);   // a 3 s gap counts 1 s
        Assert.Equal(100, grid[2 * MatchTimeline.MapWidth + 6]);   // the last record holds no time
        Assert.Equal(1_300, grid.Sum());
    }

    [Fact]
    public void Time_off_the_grid_is_clamped_to_its_edge()
    {
        var grid = MatchTimeline.TimeSpent([Life((0, -5, 500), (1, 0, 0))], TickMs, Cell);
        Assert.Equal(100, grid[(MatchTimeline.MapHeight - 1) * MatchTimeline.MapWidth]);
    }

    [Fact]
    public void Heat_levels_spread_time_to_neighbours_and_rank_cells_into_equal_groups()
    {
        var grid = new float[MatchTimeline.MapWidth * MatchTimeline.MapHeight];
        grid[10 * MatchTimeline.MapWidth + 10] = 1_000;
        grid[40 * MatchTimeline.MapWidth + 60] = 50;
        var levels = MatchTimeline.HeatLevels(grid, levels: 2, radius: 1);

        Assert.Equal(18, levels.Count(l => l > 0));                           // two 3×3 blocks
        Assert.Equal(2, levels[11 * MatchTimeline.MapWidth + 11]);            // next to the busy cell
        Assert.Equal(1, levels[40 * MatchTimeline.MapWidth + 60]);
        Assert.Equal(0, levels[0]);
        Assert.Equal(9, levels.Count(l => l == 2));
    }

    [Fact]
    public void The_aim_at_a_tick_is_the_last_one_recorded_at_or_before_it()
    {
        List<(int, Aim)> aims = [(2, new Aim(10, 2048)), (5, new Aim(20, 2048)), (9, new Aim(30, 2048))];

        Assert.Null(MatchTimeline.AimAt(aims, 1));
        Assert.Equal(10, MatchTimeline.AimAt(aims, 2)!.Value.Aim.Yaw);
        Assert.Equal(20, MatchTimeline.AimAt(aims, 8)!.Value.Aim.Yaw);
        Assert.Equal(30, MatchTimeline.AimAt(aims, 100)!.Value.Aim.Yaw);
        Assert.Null(MatchTimeline.AimAt([], 3));
    }

    [Theory]
    [InlineData(10, 350, 20)]
    [InlineData(0, 180, 180)]
    [InlineData(-90, 90, 180)]
    [InlineData(45, 45, 0)]
    [InlineData(720, 30, 30)]
    public void Angles_between_directions_wrap_round(double a, double b, double expected) =>
        Assert.Equal(expected, MatchTimeline.AngleBetween(a, b), 6);

    [Fact]
    public void Angle_off_is_measured_from_the_victims_facing_to_the_killer()
    {
        // The map's Y runs down, so a killer drawn above the victim is at +90° (the film's +Y).
        DeathPoint Death(double facing, double kx, double ky) =>
            new(0, 0, 0, 1, new MapPoint(10, 10), facing, new MapPoint(kx, ky), null);

        Assert.Equal(0, Death(90, 10, 4).AngleOff!.Value, 6);     // facing up, killer above
        Assert.Equal(180, Death(270, 10, 4).AngleOff!.Value, 6);  // facing down, killer above
        Assert.Equal(90, Death(0, 10, 4).AngleOff!.Value, 6);     // facing right, killer above
        Assert.Equal(0, Death(0, 15, 10).AngleOff!.Value, 6);     // facing right, killer to the right
        Assert.Null((Death(0, 15, 10) with { Facing = null }).AngleOff);
        Assert.Null(Death(0, 10, 10).AngleOff);                    // same spot: no direction
    }
}
