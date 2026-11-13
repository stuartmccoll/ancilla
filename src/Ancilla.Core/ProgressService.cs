namespace Ancilla.Core;

/// <summary>Pulls everything needed and turns it into a <see cref="ProgressReport"/>.</summary>
public sealed class ProgressService(HaloWaypointClient client, SnapshotStore snapshots)
{
    /// <param name="xpPerMatchOverride">Use this XP/match instead of measuring it.</param>
    public async Task<ProgressReport> BuildReportAsync(double? xpPerMatchOverride = null, CancellationToken ct = default)
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

        var estimate = xpPerMatchOverride is { } o
            ? new XpPerMatchEstimate(o, "supplied manually")
            : ProgressMath.EstimateXpPerMatch(history, career.XpEarned, matches);

        return new ProgressReport(me.Gamertag, matches, career, passes, estimate);
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
    /// Prefers a measured rate from snapshots (needs at least <paramref name="minMatches"/> matches
    /// between the oldest snapshot in the window and now). Falls back to the lifetime average,
    /// which is only a rough guide.
    /// </summary>
    public static XpPerMatchEstimate EstimateXpPerMatch(
        IReadOnlyList<ProgressSnapshot> history, long careerXpNow, int matchesNow,
        int minMatches = 5, int windowDays = 60)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-windowDays);
        var baseline = history
            .Where(s => s.TakenAt >= cutoff && matchesNow - s.MatchesCompleted >= minMatches)
            .OrderBy(s => s.TakenAt)
            .FirstOrDefault();

        if (baseline is not null)
        {
            var games = matchesNow - baseline.MatchesCompleted;
            var xp = careerXpNow - baseline.CareerXpEarned;
            return new XpPerMatchEstimate((double)xp / games,
                $"measured: {xp:N0} XP over {games} matches since {baseline.TakenAt:yyyy-MM-dd}");
        }

        return new XpPerMatchEstimate(
            matchesNow == 0 ? 0 : (double)careerXpNow / matchesNow,
            "lifetime average (rough: play a few games and re-run for a measured rate)");
    }
}
