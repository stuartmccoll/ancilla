using Ancilla.Core;

namespace Ancilla.Core.Tests;

public class ProgressMathTests
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
        var r = new ProgressReport("Noble Six", 110, career, [Pass("pass/a", 48_000)], new XpPerMatchEstimate(8_000, "test"), []);
        Assert.Null(r.PassGamesFor(44_000));
        Assert.Equal(55, (r with { PassEstimate = new XpPerMatchEstimate(800, "test") }).PassGamesFor(44_000));
        Assert.Equal(6, r.GamesFor(44_000));   // what the page used to show for the pass
    }

    // ---- Rank costs, tracks and career ---------------------------------------------------

    private static RewardTrackDefinition Def(int? xpPerRank, params int?[] costs) =>
        new("track", "Operation: Fall of Reach", xpPerRank, costs.Select((c, i) => new RankDefinition(i + 1, c, null, null, null)).ToList());

    private static RewardTrackProgress Progress(int rank, int partial, bool max = false, bool owned = true) =>
        new("RewardTracks/Operations/OpFallOfReach.json", "Operation", new TrackProgressState(rank, partial, owned, max), owned);

    [Fact]
    public void Per_rank_costs_win_over_the_flat_xp_per_rank()
    {
        Assert.Equal([500, 750, 1000], ProgressMath.RankCosts(Def(1000, 500, 750, 1000)));
        Assert.Equal([1000, 1000, 1000], ProgressMath.RankCosts(Def(1000, null, 0, null)));
        Assert.Equal([0, 0], ProgressMath.RankCosts(Def(null, null, null)));
    }

    [Fact]
    public void Track_counts_completed_ranks_plus_partial_progress()
    {
        var t = ProgressMath.Track(Progress(2, 300), Def(1000, null, null, null, null), isActive: true);
        Assert.Equal(2, t.RanksCompleted);
        Assert.Equal(4, t.TotalRanks);
        Assert.Equal(2_300, t.XpEarned);
        Assert.Equal(4_000, t.XpTotal);
        Assert.Equal(1_700, t.XpRemaining);
        Assert.False(t.IsComplete);
        Assert.True(t.IsActive);
        Assert.Equal("Operation: Fall of Reach", t.Name);
    }

    [Fact]
    public void Track_at_max_rank_counts_as_complete_with_all_its_xp()
    {
        var t = ProgressMath.Track(Progress(4, 0, max: true), Def(1000, null, null, null, null), isActive: false);
        Assert.True(t.IsComplete);
        Assert.Equal(t.XpTotal, t.XpEarned);
        Assert.Equal(0, t.XpRemaining);
        Assert.False(t.IsNotStarted);
    }

    [Fact]
    public void Track_falls_back_to_its_id_when_it_has_no_name()
    {
        var def = Def(1000, (int?)null) with { Name = " " };
        Assert.Equal("track", ProgressMath.Track(Progress(0, 0), def, false).Name);
        Assert.True(ProgressMath.Track(Progress(0, 0, owned: false), def, false).IsNotStarted);
    }

    [Fact]
    public void Career_names_the_next_rank_and_its_icon()
    {
        var def = new RewardTrackDefinition("careerRank1", "Career", 1000,
        [
            new(1, 500, "Recruit", null, null, "r1.png"),
            new(2, 750, "Cadet", "Bronze", "2", "r2.png"),
            new(3, 1000, "Hero", null, null, "hero.png"),
        ]);
        var c = ProgressMath.Career(Progress(1, 100), def);
        Assert.Equal(1, c.RanksCompleted);
        Assert.Equal("Cadet Bronze 2", c.NextRankTitle);
        Assert.Equal("r2.png", c.RankIcon);
        Assert.Equal(600, c.XpEarned);
        Assert.Equal(2_250, c.XpTotal);
        Assert.False(c.IsHero);

        var hero = ProgressMath.Career(Progress(3, 0), def);
        Assert.True(hero.IsHero);
        Assert.Equal("Hero", hero.NextRankTitle);
        Assert.Equal("hero.png", hero.RankIcon);
        Assert.Equal(0, hero.XpRemaining);
    }

    // ---- Windows and baselines ------------------------------------------------------------

    [Fact]
    public void Window_lookup_falls_back_to_the_default()
    {
        Assert.Equal("d60", XpAverageWindow.Default.Key);
        Assert.Equal("m10", XpAverageWindow.Find("m10").Key);
        Assert.Equal(XpAverageWindow.Default, XpAverageWindow.Find("not-a-window"));
        Assert.Equal(XpAverageWindow.Default, XpAverageWindow.Find(null));
    }

    [Fact]
    public void Last_n_matches_uses_the_latest_snapshot_far_enough_back()
    {
        ProgressSnapshot[] history =
        [
            new(Day0, 100, 100_000),
            new(Day0.AddDays(1), 110, 150_000),
            new(Day0.AddDays(2), 118, 190_000),
        ];
        var baseline = ProgressMath.FindBaseline(history, 200_000, 120, XpAverageWindow.Find("m10"), out var partial);
        Assert.Equal(110, baseline!.MatchesCompleted);
        Assert.False(partial);

        var est = ProgressMath.EstimateXpPerMatch(history, 200_000, 120, XpAverageWindow.Find("m10"));
        Assert.Equal(5_000, est.XpPerMatch);
        Assert.StartsWith("Measured: 50,000 XP over 10 matches", est.Source);
    }

    [Fact]
    public void Too_little_history_for_the_window_says_so()
    {
        ProgressSnapshot[] history = [new(Day0, 100, 100_000)];
        var baseline = ProgressMath.FindBaseline(history, 160_000, 112, XpAverageWindow.Find("m50"), out var partial);
        Assert.Equal(100, baseline!.MatchesCompleted);
        Assert.True(partial);
        Assert.Contains("only 12 recorded so far",
            ProgressMath.EstimateXpPerMatch(history, 160_000, 112, XpAverageWindow.Find("m50")).Source);
    }

    [Fact]
    public void Baselines_need_at_least_five_matches_and_no_more_xp_than_now()
    {
        ProgressSnapshot[] history =
        [
            new(Day0, 100, 100_000),
            new(Day0.AddDays(1), 118, 999_999),   // more XP than now: a different account or bad data
        ];
        Assert.Null(ProgressMath.FindBaseline(history, 200_000, 103, XpAverageWindow.Find("all"), out _));
        Assert.Equal(100, ProgressMath.FindBaseline(history, 200_000, 120, XpAverageWindow.Find("all"), out _)!.MatchesCompleted);
    }

    [Fact]
    public void Last_n_days_ignores_older_snapshots()
    {
        var now = DateTimeOffset.UtcNow;
        ProgressSnapshot[] history = [new(now.AddDays(-20), 100, 100_000), new(now.AddDays(-3), 110, 150_000)];
        Assert.Equal(110, ProgressMath.FindBaseline(history, 200_000, 120, XpAverageWindow.Find("d7"), out _)!.MatchesCompleted);
        Assert.Equal(100, ProgressMath.FindBaseline(history, 200_000, 120, XpAverageWindow.Find("d30"), out _)!.MatchesCompleted);
    }

    [Fact]
    public void Lifetime_average_is_the_fallback()
    {
        var life = ProgressMath.EstimateXpPerMatch([], 1_000_000, 500, XpAverageWindow.Find("life"));
        Assert.Equal(2_000, life.XpPerMatch);
        Assert.Equal("Lifetime average, a rough guide", life.Source);

        var noHistory = ProgressMath.EstimateXpPerMatch([], 1_000_000, 500, XpAverageWindow.Find("m10"));
        Assert.Contains("there aren't enough snapshots", noHistory.Source);
        Assert.Equal(0, ProgressMath.EstimateXpPerMatch([], 0, 0, XpAverageWindow.Find("life")).XpPerMatch);
    }

    [Fact]
    public void A_manual_figure_overrides_the_measurement()
    {
        var e = ProgressMath.Estimate([], 1_000_000, 500, XpAverageWindow.Default, manual: 1_234);
        Assert.Equal(1_234, e.XpPerMatch);
        Assert.Equal("Set manually", e.Source);
        Assert.NotEqual("Set manually", ProgressMath.Estimate([], 1_000_000, 500, XpAverageWindow.Default, manual: 0).Source);
    }

    [Fact]
    public void A_finished_pass_has_no_pass_xp_rate()
    {
        ProgressSnapshot[] history = [new(Day0, 100, 1_000_000, "pass/a", 40_000)];
        var done = Pass("pass/a", 100_000) with { IsComplete = true };
        Assert.Null(ProgressMath.EstimatePassXpPerMatch(history, done, 120, XpAverageWindow.Find("all")));
    }

    [Fact]
    public void With_estimate_recalculates_without_touching_the_rest()
    {
        ProgressSnapshot[] history = [new(Day0, 100, 100_000, "pass/a", 10_000)];
        var career = new CareerStatus(10, 272, "Next", null, 200_000, 10_000_000, false);
        var r = new ProgressReport("Noble Six", 120, career, [Pass("pass/a", 20_000)], new XpPerMatchEstimate(1, "old"), history);
        var updated = ProgressMath.WithEstimate(r, XpAverageWindow.Find("all"), manual: null);
        Assert.Equal(5_000, updated.Estimate.XpPerMatch);
        Assert.Equal(500, updated.PassEstimate!.XpPerMatch);
        Assert.Equal(r.LoadedAt, updated.LoadedAt);
        Assert.Same(r.Passes, updated.Passes);
    }

    // ---- Hero forecast ----------------------------------------------------------------------

    [Fact]
    public void Hero_forecast_extends_the_daily_pace()
    {
        ProgressSnapshot[] history = [new(Day0, 100, 1_000_000), new(Day0.AddDays(10), 150, 1_100_000)];
        var f = ProgressMath.ForecastHero(history, 2_100_000)!;
        Assert.Equal(10_000, f.XpPerDay, 3);
        Assert.Equal(5, f.MatchesPerDay, 3);
        Assert.Equal(2_000, f.XpPerMatch, 3);
        Assert.Equal(100, f.DaysToHero, 3);
        Assert.Equal(Day0.AddDays(110), f.Eta);
        Assert.False(f.IsFallback);
    }

    [Fact]
    public void Hero_forecast_needs_a_day_and_some_xp()
    {
        Assert.Null(ProgressMath.ForecastHero([new(Day0, 1, 1)], 100));
        Assert.Null(ProgressMath.ForecastHero([new(Day0, 1, 1), new(Day0.AddHours(12), 2, 50)], 100));
        Assert.Null(ProgressMath.ForecastHero([new(Day0, 1, 50), new(Day0.AddDays(3), 2, 50)], 100));
    }

    [Fact]
    public void Hero_forecast_beyond_thirty_years_has_no_date()
    {
        ProgressSnapshot[] history = [new(Day0, 100, 1_000), new(Day0.AddDays(1), 101, 1_001)];
        var f = ProgressMath.ForecastHero(history, 100_000_000)!;
        Assert.Null(f.Eta);
    }

    [Fact]
    public void Hero_forecast_falls_back_when_the_chosen_start_is_too_recent()
    {
        ProgressSnapshot[] history = [new(Day0, 100, 1_000_000), new(Day0.AddDays(10), 150, 1_100_000)];
        var f = ProgressMath.ForecastHero(history, 2_100_000, from: history[1])!;
        Assert.True(f.IsFallback);
        Assert.Equal(history[0], f.WindowStart);
    }
}
