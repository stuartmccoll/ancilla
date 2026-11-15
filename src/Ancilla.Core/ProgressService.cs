namespace Ancilla.Core;

/// <summary>Pulls everything needed and turns it into a <see cref="ProgressReport"/>.</summary>
public sealed class ProgressService(HaloWaypointClient client, SnapshotStore snapshots)
{
    /// <param name="window">Which stretch of history to average XP per match over.</param>
    /// <param name="xpPerMatchOverride">Use this XP/match instead of measuring it.</param>
    public async Task<ProgressReport> BuildReportAsync(
        XpAverageWindow? window = null, double? xpPerMatchOverride = null, CancellationToken ct = default)
    {
        var me = await client.GetMeAsync(ct);
        var xuid = me.Xuid;

        var careerTask = client.GetCareerRankAsync(xuid, ct);
        var opsTask = client.GetOperationsAsync(xuid, ct);
        var srTask = client.GetServiceRecordAsync(xuid, ct);
        await Task.WhenAll(careerTask, opsTask, srTask);

        var careerProgress = careerTask.Result;
        var careerDef = await client.GetRewardTrackDefinitionAsync(careerProgress.RewardTrackPath, ct);
        var career = ProgressMath.Career(careerProgress, careerDef);

        var ops = opsTask.Result;
        var defs = await Task.WhenAll(ops.OperationRewardTracks.Select(async t =>
        {
            // Some legacy track files may be missing or restricted; skip those rather than fail.
            try { return await client.GetRewardTrackDefinitionAsync(t.RewardTrackPath, ct); }
            catch (HttpRequestException) { return (RewardTrackDefinition?)null; }
        }));
        var passes = ops.OperationRewardTracks
            .Zip(defs, (p, d) => (p, d))
            .Where(x => x.d?.Ranks is { Count: > 0 })
            .Select(x => ProgressMath.Track(x.p, x.d!, x.p.RewardTrackPath == ops.ActiveOperationRewardTrackPath))
            .ToList();

        var matches = srTask.Result.MatchesCompleted;

        // Record a snapshot so future runs can measure actual XP gained per match.
        var now = new ProgressSnapshot(DateTimeOffset.UtcNow, matches, career.XpEarned);
        var history = await snapshots.AppendAsync(now, ct);

        var estimate = ProgressMath.Estimate(history, career.XpEarned, matches, window ?? XpAverageWindow.Default, xpPerMatchOverride);
        return new ProgressReport(me.Gamertag, matches, career, passes, estimate, history);
    }
}

public static class ProgressMath
{
    /// <summary>XP needed for each rank of a track (operations use a flat XpPerRank).</summary>
    public static int[] RankCosts(RewardTrackDefinition def) =>
        // Per-rank costs win when present: the career track also carries a placeholder
        // XpPerRank of 1000 that doesn't reflect its real, escalating rank costs.
        def.Ranks.Select(r => r.XpRequiredForRank is > 0 ? r.XpRequiredForRank.Value : def.XpPerRank ?? 0).ToArray();

    public static TrackStatus Track(RewardTrackProgress p, RewardTrackDefinition def, bool isActive)
    {
        var costs = RankCosts(def);
        var done = Math.Min(p.CurrentProgress.Rank, costs.Length);
        var earned = costs.Take(done).Sum() + p.CurrentProgress.PartialProgress;
        var complete = p.CurrentProgress.HasReachedMaxRank || done >= costs.Length;
        var name = string.IsNullOrWhiteSpace(def.Name) ? def.TrackId : def.Name!;
        return new TrackStatus(p.RewardTrackPath, name, done, costs.Length,
            complete ? costs.Sum() : earned, costs.Sum(), p.IsOwned, complete, isActive);
    }

    /// <summary>Hero is the final career rank (272). Rank = ranks completed, as with passes.</summary>
    public static CareerStatus Career(RewardTrackProgress p, RewardTrackDefinition def)
    {
        var costs = RankCosts(def).Select(c => (long)c).ToArray();
        var done = Math.Min(p.CurrentProgress.Rank, costs.Length);
        var earned = costs.Take(done).Sum() + p.CurrentProgress.PartialProgress;
        var isHero = p.CurrentProgress.HasReachedMaxRank || done >= costs.Length;
        var next = def.Ranks.ElementAtOrDefault(done);
        var nextTitle = next is null ? "Hero" :
            string.Join(' ', new[] { next.RankTitle, next.RankSubTitle, next.RankTier }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var icon = (next ?? def.Ranks.LastOrDefault())?.RankIcon;
        return new CareerStatus(done, costs.Length, nextTitle, icon, isHero ? costs.Sum() : earned, costs.Sum(), isHero);
    }

    /// <summary>
    /// Average career XP per day from <paramref name="from"/> to the latest snapshot, counting days
    /// without play, extended in a straight line to Hero. Without a usable <paramref name="from"/>
    /// (none given, under a day ago, or no XP gained since), the earliest snapshot in the last
    /// <paramref name="fallbackDays"/> days is used instead. Null if neither works.
    /// </summary>
    /// <param name="snapshots">Oldest first.</param>
    public static HeroForecast? ForecastHero(
        IReadOnlyList<ProgressSnapshot> snapshots, long xpTotal, ProgressSnapshot? from = null, int fallbackDays = 60)
    {
        if (snapshots.Count < 2) return null;
        var last = snapshots[^1];
        if (from is not null && Pace(from, last, xpTotal) is { } chosen) return chosen;
        var first = snapshots.FirstOrDefault(s => s.TakenAt >= last.TakenAt.AddDays(-fallbackDays)) ?? snapshots[0];
        return Pace(first, last, xpTotal) is { } fallback ? fallback with { IsFallback = from is not null } : null;
    }

    private static HeroForecast? Pace(ProgressSnapshot first, ProgressSnapshot last, long xpTotal)
    {
        var days = (last.TakenAt - first.TakenAt).TotalDays;
        var xp = last.CareerXpEarned - first.CareerXpEarned;
        if (days < 1 || xp <= 0) return null;
        var matches = last.MatchesCompleted - first.MatchesCompleted;
        var perDay = xp / days;
        var toHero = Math.Max(0, xpTotal - last.CareerXpEarned) / perDay;
        DateTimeOffset? eta = toHero <= 365 * 30 ? last.TakenAt.AddDays(toHero) : null;
        return new HeroForecast(first, perDay, matches / days, matches == 0 ? 0 : (double)xp / matches, days, toHero, eta);
    }

    /// <summary>Recalculates a report's XP per match for a different window, without calling the API.</summary>
    public static ProgressReport WithEstimate(ProgressReport r, XpAverageWindow window, double? manual) =>
        r with { Estimate = Estimate(r.History, r.Career.XpEarned, r.MatchesCompleted, window, manual) };

    public static XpPerMatchEstimate Estimate(
        IReadOnlyList<ProgressSnapshot> history, long careerXpNow, int matchesNow,
        XpAverageWindow window, double? manual) =>
        manual is > 0 ? new XpPerMatchEstimate(manual.Value, "Set manually")
            : EstimateXpPerMatch(history, careerXpNow, matchesNow, window);

    /// <summary>
    /// The snapshot a window measures from. The API has no per-match XP, so this is the snapshot
    /// that best fits: for "last N matches", the latest one at least N matches back (or the oldest
    /// available, with <paramref name="partial"/> set, if there isn't that much history); for "last
    /// N days", the earliest one inside those days; for all history, the oldest. It needs at least
    /// <paramref name="minMatches"/> matches since it, or the figure is too noisy. Null for the
    /// lifetime window, or when nothing fits.
    /// </summary>
    public static ProgressSnapshot? FindBaseline(
        IReadOnlyList<ProgressSnapshot> history, long careerXpNow, int matchesNow,
        XpAverageWindow window, out bool partial, int minMatches = 5)
    {
        partial = false;
        if (window.Lifetime) return null;
        var usable = history
            .Where(s => matchesNow - s.MatchesCompleted >= minMatches && s.CareerXpEarned <= careerXpNow)
            .OrderBy(s => s.TakenAt)
            .ToList();

        if (window.LastMatches is { } n)
        {
            var exact = usable.LastOrDefault(s => matchesNow - s.MatchesCompleted >= n);
            partial = exact is null && usable.Count > 0;
            return exact ?? usable.FirstOrDefault();
        }
        if (window.LastDays is { } d)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-d);
            return usable.FirstOrDefault(s => s.TakenAt >= cutoff);
        }
        return usable.FirstOrDefault();
    }

    /// <summary>
    /// XP per match between the window's baseline snapshot (see <see cref="FindBaseline"/>) and now.
    /// Falls back to the lifetime average, which is only a rough guide.
    /// </summary>
    public static XpPerMatchEstimate EstimateXpPerMatch(
        IReadOnlyList<ProgressSnapshot> history, long careerXpNow, int matchesNow,
        XpAverageWindow window, int minMatches = 5)
    {
        if (FindBaseline(history, careerXpNow, matchesNow, window, out var partial, minMatches) is { } baseline)
        {
            var games = matchesNow - baseline.MatchesCompleted;
            var xp = careerXpNow - baseline.CareerXpEarned;
            var shortNote = partial ? $"; only {games} recorded so far" : "";
            return new XpPerMatchEstimate((double)xp / games,
                $"Measured: {xp:N0} XP over {games:N0} matches since {baseline.TakenAt.ToLocalTime():d MMM yyyy}{shortNote}");
        }

        var lifetime = matchesNow == 0 ? 0 : (double)careerXpNow / matchesNow;
        return new XpPerMatchEstimate(lifetime, window.Lifetime
            ? "Lifetime average, a rough guide"
            : "Lifetime average, a rough guide: there aren't enough snapshots in this window yet");
    }
}

/// <summary>When Hero is reached at the recent daily pace. <see cref="Eta"/> is null beyond 30 years.</summary>
/// <see cref="IsFallback"/> means the chosen window was too short, so the default window was used.
public sealed record HeroForecast(ProgressSnapshot WindowStart, double XpPerDay, double MatchesPerDay, double XpPerMatch,
    double WindowDays, double DaysToHero, DateTimeOffset? Eta, bool IsFallback = false);

/// <summary>A stretch of snapshot history to average XP per match over.</summary>
public sealed record XpAverageWindow(string Key, string Label, int? LastMatches = null, int? LastDays = null, bool Lifetime = false)
{
    public static readonly IReadOnlyList<XpAverageWindow> All =
    [
        new("m10", "Last 10 matches", LastMatches: 10),
        new("m50", "Last 50 matches", LastMatches: 50),
        new("d7", "Last 7 days", LastDays: 7),
        new("d30", "Last 30 days", LastDays: 30),
        new("d60", "Last 60 days", LastDays: 60),
        new("d365", "Last 12 months", LastDays: 365),
        new("all", "All recorded history"),
        new("life", "Lifetime average", Lifetime: true),
    ];

    /// <summary>Last 60 days, which matches how the rate was always measured.</summary>
    public static XpAverageWindow Default => All[4];

    public static XpAverageWindow Find(string? key) => All.FirstOrDefault(w => w.Key == key) ?? Default;
}
