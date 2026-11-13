using System.Text.Json.Serialization;

namespace Ancilla.Core;

// ---- Raw API shapes (only the fields we use) -------------------------------

/// <summary>GET profile.svc.halowaypoint.com/users/me</summary>
public sealed record UserProfile(string Xuid, string Gamertag, Gamerpic? Gamerpic = null);
public sealed record Gamerpic(string? Small, string? Medium, string? Large, string? Xlarge);

/// <summary>GET settings.svc.halowaypoint.com/oban/flight-configurations/titles/hi/audiences/RETAIL/active</summary>
public sealed record FlightConfiguration(string FlightConfigurationId);

/// <summary>Player progress on one reward track (career rank, operation / battle pass).</summary>
public sealed record RewardTrackProgress(
    string RewardTrackPath,
    string TrackType,
    TrackProgressState CurrentProgress,
    bool IsOwned);

/// <summary>
/// Rank = number of ranks completed; PartialProgress = XP into the next rank.
/// e.g. Rank 45, PartialProgress 700 on a 1000-XP-per-rank pass = 45,700 XP.
/// </summary>
public sealed record TrackProgressState(
    int Rank,
    int PartialProgress,
    bool IsOwned,
    bool HasReachedMaxRank);

/// <summary>GET economy.svc.halowaypoint.com/hi/players/xuid({xuid})/rewardtracks/operations</summary>
public sealed record OperationsResponse(
    string ActiveOperationRewardTrackPath,
    List<RewardTrackProgress> OperationRewardTracks);

/// <summary>GET gamecms-hacs.svc.halowaypoint.com/hi/Progression/file/{RewardTrackPath}</summary>
public sealed record RewardTrackDefinition(
    string TrackId,
    [property: JsonConverter(typeof(LocalizedStringConverter))] string? Name,
    int? XpPerRank,
    List<RankDefinition> Ranks);

public sealed record RankDefinition(
    int Rank,
    int? XpRequiredForRank,
    [property: JsonConverter(typeof(LocalizedStringConverter))] string? RankTitle,
    [property: JsonConverter(typeof(LocalizedStringConverter))] string? RankSubTitle,
    [property: JsonConverter(typeof(LocalizedStringConverter))] string? RankTier,
    string? RankIcon = null,
    string? RankLargeIcon = null,
    string? TierType = null,
    int RankGrade = 0);


// ---- Computed results ------------------------------------------------------

public sealed record TrackStatus(
    string Path,
    string Name,
    int RanksCompleted,
    int TotalRanks,
    int XpEarned,
    int XpTotal,
    bool IsOwned,
    bool IsComplete,
    bool IsActive)
{
    public int XpRemaining => Math.Max(0, XpTotal - XpEarned);
}

public sealed record CareerStatus(
    int RanksCompleted,
    int TotalRanks,
    string NextRankTitle,
    string? RankIcon,
    long XpEarned,
    long XpTotal,
    bool IsHero)
{
    public long XpRemaining => Math.Max(0, XpTotal - XpEarned);
}

public sealed record XpPerMatchEstimate(double XpPerMatch, string Source);

public sealed record ProgressReport(
    string Gamertag,
    int MatchesCompleted,
    CareerStatus Career,
    IReadOnlyList<TrackStatus> Passes,
    XpPerMatchEstimate Estimate)
{
    public IEnumerable<TrackStatus> RemainingPasses(bool includeUnowned) =>
        Passes.Where(p => !p.IsComplete && (includeUnowned || p.IsOwned));

    public int GamesFor(long xp) =>
        xp <= 0 ? 0 : (int)Math.Ceiling(xp / Math.Max(1, Estimate.XpPerMatch));
}

/// <summary>A point-in-time reading, used to measure real XP gained per match.</summary>
public sealed record ProgressSnapshot(DateTimeOffset TakenAt, int MatchesCompleted, long CareerXpEarned);
