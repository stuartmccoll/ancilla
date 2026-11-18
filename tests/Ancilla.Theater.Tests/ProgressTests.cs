using Ancilla.Core;

namespace Ancilla.Theater.Tests;

// ProgressMath lives in Core, which has no test project of its own.
public class ProgressTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static TrackStatus Pass(string path, int earned, bool active = true) =>
        new(path, "Test pass", earned / 1000, 100, earned, 100_000, true, false, active);

    [Fact]
    public void Pass_xp_per_match_is_measured_from_pass_xp_not_career_xp()
    {
        // Career XP grows about ten times as fast as pass XP.
        ProgressSnapshot[] history =
        [
            new(Day0, 100, 1_000_000, "pass/a", 40_000),
            new(Day0.AddDays(1), 104, 1_032_000, "pass/a", 43_200),
            new(Day0.AddDays(2), 110, 1_080_000, "pass/a", 48_000),
        ];
        var window = XpAverageWindow.Find("all");
        var career = ProgressMath.EstimateXpPerMatch(history, 1_080_000, 110, window);
        var pass = ProgressMath.EstimatePassXpPerMatch(history, Pass("pass/a", 48_000), 110, window);

        Assert.Equal(8_000, career.XpPerMatch);
        Assert.Equal(800, pass!.XpPerMatch);
    }

    [Fact]
    public void Pass_xp_per_match_needs_history_with_the_same_pass_active()
    {
        ProgressSnapshot[] history =
        [
            new(Day0, 100, 1_000_000),                                   // saved before pass XP was recorded
            new(Day0.AddDays(1), 104, 1_032_000, "pass/old", 90_000),    // another pass was active
            new(Day0.AddDays(2), 108, 1_064_000, "pass/a", 2_000),
        ];
        var window = XpAverageWindow.Find("all");
        Assert.Null(ProgressMath.EstimatePassXpPerMatch(history, Pass("pass/a", 3_000), 110, window));   // 2 matches since: too few
        Assert.NotNull(ProgressMath.EstimatePassXpPerMatch(history, Pass("pass/a", 6_000), 113, window));
        Assert.Null(ProgressMath.EstimatePassXpPerMatch(history, null, 113, window));
    }

    [Fact]
    public void Matches_for_passes_are_unknown_until_measured()
    {
        var career = new CareerStatus(10, 272, "Next", null, 100_000, 10_000_000, false);
        var r = new ProgressReport("someone", 110, career, [Pass("pass/a", 48_000)], new XpPerMatchEstimate(8_000, "test"), []);
        Assert.Null(r.PassGamesFor(44_000));
        Assert.Equal(55, (r with { PassEstimate = new XpPerMatchEstimate(800, "test") }).PassGamesFor(44_000));
        Assert.Equal(6, r.GamesFor(44_000));   // what the page used to show for the pass
    }
}
