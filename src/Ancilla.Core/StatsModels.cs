using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace Ancilla.Core;

// ---- Service record ----------------------------------------------------------

/// <summary>GET halostats.svc.halowaypoint.com/hi/players/xuid({xuid})/Matchmade/servicerecord</summary>
public sealed class ServiceRecord
{
    public string TimePlayed { get; init; } = "PT0S";
    public int MatchesCompleted { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int Ties { get; init; }
    public CoreStats CoreStats { get; init; } = new();
    public ServiceRecordSubqueries? Subqueries { get; init; }

    /// <summary>
    /// Per-mode groups (CaptureTheFlagStats, OddballStats, ZonesStats, PveStats, ...). Kept
    /// generic because the set changes as modes are added.
    /// </summary>
    [JsonExtensionData] public Dictionary<string, JsonElement> ModeStats { get; init; } = new();
}

public sealed record ServiceRecordSubqueries(List<string>? PlaylistAssetIds);

/// <summary>Shared by the service record (lifetime totals) and match stats (one game).</summary>
public sealed class CoreStats
{
    public long Score { get; init; }
    public long PersonalScore { get; init; }
    public int RoundsWon { get; init; }
    public int RoundsLost { get; init; }
    public int RoundsTied { get; init; }
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    /// <summary>Per match.</summary>
    public double? KDA { get; init; }
    /// <summary>Service record only.</summary>
    public double? AverageKDA { get; init; }
    public int Suicides { get; init; }
    public int Betrayals { get; init; }
    public string? AverageLifeDuration { get; init; }
    public int GrenadeKills { get; init; }
    public int HeadshotKills { get; init; }
    public int MeleeKills { get; init; }
    public int PowerWeaponKills { get; init; }
    public long ShotsFired { get; init; }
    public long ShotsHit { get; init; }
    public double Accuracy { get; init; }
    public long DamageDealt { get; init; }
    public long DamageTaken { get; init; }
    public int CalloutAssists { get; init; }
    public int VehicleDestroys { get; init; }
    public int DriverAssists { get; init; }
    public int Hijacks { get; init; }
    public int EmpAssists { get; init; }
    public int MaxKillingSpree { get; init; }
    public int Spawns { get; init; }
    public int ObjectivesCompleted { get; init; }
    public List<MedalCount> Medals { get; init; } = [];

    [JsonIgnore] public double KillDeathRatio => Deaths == 0 ? Kills : (double)Kills / Deaths;
}

/// <summary>NameId is a 32-bit hash that can exceed int.MaxValue.</summary>
public sealed record MedalCount(long NameId, int Count, int TotalPersonalScoreAwarded);

/// <summary>A block of stats: CoreStats plus whichever mode-specific groups apply.</summary>
public sealed class StatsBlock
{
    public CoreStats CoreStats { get; init; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement> ModeStats { get; init; } = new();
}

// ---- Matches -------------------------------------------------------------------

public enum MatchOutcome { Unknown = 0, Tie = 1, Win = 2, Loss = 3, Left = 4 }

public enum MatchHistoryType { All, Matchmaking, Custom, Local }

/// <summary>Reference to a versioned UGC asset (map, game variant, playlist).</summary>
public sealed record AssetRef(int AssetKind, string AssetId, string VersionId);

/// <summary>GET halostats.svc.halowaypoint.com/hi/players/xuid({xuid})/matches?start=N&amp;count=N&amp;type=T</summary>
public sealed record MatchHistory(int Start, int Count, int ResultCount, List<MatchHistoryEntry> Results);

public sealed record MatchHistoryEntry(
    string MatchId,
    MatchInfo MatchInfo,
    int LastTeamId,
    MatchOutcome Outcome,
    int Rank,
    bool PresentAtEndOfMatch);

public sealed record MatchInfo(
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    string? ClearanceId,
    string? Duration = null,
    int GameVariantCategory = 0,
    AssetRef? MapVariant = null,
    AssetRef? UgcGameVariant = null,
    AssetRef? Playlist = null,
    int? PlaylistExperience = null,
    bool TeamsEnabled = false);

/// <summary>GET halostats.svc.halowaypoint.com/hi/matches/{matchId}/stats</summary>
public sealed record MatchStats(string MatchId, MatchInfo MatchInfo, List<MatchTeam> Teams, List<MatchPlayer> Players)
{
    /// <summary>
    /// Firefight and other games against AI only: <paramref name="xuid"/>'s stats have a PvE group and
    /// no PvP one. Kills there aren't comparable to PvP.
    /// </summary>
    public bool IsPve(string xuid) =>
        Players.FirstOrDefault(p => p.Xuid == xuid)?.Stats?.ModeStats is { } m
        && m.ContainsKey("PveStats") && !m.ContainsKey("PvpStats");
}

public sealed record MatchTeam(int TeamId, MatchOutcome Outcome, int Rank, StatsBlock Stats);

public sealed record MatchPlayer(
    string PlayerId,
    int PlayerType,
    int LastTeamId,
    MatchOutcome Outcome,
    int Rank,
    ParticipationInfo? ParticipationInfo,
    List<PlayerTeamStats> PlayerTeamStats)
{
    [JsonIgnore] public bool IsHuman => PlayerType == 1;

    /// <summary>"xuid(123)" → "123"; null for bots ("bid(...)").</summary>
    [JsonIgnore] public string? Xuid => PlayerId.StartsWith("xuid(", StringComparison.Ordinal) ? PlayerId[5..^1] : null;

    [JsonIgnore] public StatsBlock? Stats =>
        (PlayerTeamStats.FirstOrDefault(t => t.TeamId == LastTeamId) ?? PlayerTeamStats.FirstOrDefault())?.Stats;
}

public sealed record ParticipationInfo(bool JoinedInProgress, bool LeftInProgress, bool PresentAtCompletion, string? TimePlayed);

public sealed record PlayerTeamStats(int TeamId, StatsBlock Stats);

// ---- Skill / CSR ---------------------------------------------------------------

/// <summary>Envelope used by skill.svc for per-player results.</summary>
public sealed record SkillResponse<T>(List<SkillResult<T>> Value);
public sealed record SkillResult<T>(string Id, int ResultCode, T? Result);

/// <summary>GET skill.svc.halowaypoint.com/hi/matches/{matchId}/skill?players=xuid(..)</summary>
public sealed record MatchSkill(
    double TeamMmr,
    RankRecap? RankRecap,
    Dictionary<string, StatPerformance>? StatPerformances,
    int TeamId,
    Dictionary<string, double>? TeamMmrs)
{
    /// <summary>
    /// This result without the stat performances and team MMRs that aren't finite numbers. The service
    /// sends infinity for some older matches; it means nothing, breaks averages and charts, and can't be
    /// written as JSON.
    /// </summary>
    public MatchSkill Finite() => this with
    {
        TeamMmr = double.IsFinite(TeamMmr) ? TeamMmr : 0,
        StatPerformances = StatPerformances?.Where(p => p.Value is { } v && double.IsFinite(v.Count) && double.IsFinite(v.Expected) && double.IsFinite(v.StdDev))
            .ToDictionary(p => p.Key, p => p.Value),
        TeamMmrs = TeamMmrs?.Where(p => double.IsFinite(p.Value)).ToDictionary(p => p.Key, p => p.Value),
    };
}

public sealed record RankRecap(Csr? PreMatchCsr, Csr? PostMatchCsr);

/// <summary>Actual vs. what the matchmaker expected for a player of your skill in that lobby.</summary>
public sealed record StatPerformance(double Count, double Expected, double StdDev);

/// <summary>GET skill.svc.halowaypoint.com/hi/playlist/{playlistId}/csrs?players=xuid(..)</summary>
public sealed record PlaylistCsr(Csr Current, Csr SeasonMax, Csr AllTimeMax);

public sealed record Csr(
    int Value,
    int MeasurementMatchesRemaining,
    string? Tier,
    int TierStart,
    int SubTier,
    string? NextTier,
    int NextTierStart,
    int NextSubTier,
    int InitialMeasurementMatches)
{
    [JsonIgnore] public bool IsRanked => Value >= 0 && !string.IsNullOrEmpty(Tier);

    /// <summary>SubTier is zero-based (Platinum 1 = SubTier 0). Onyx has no sub-tiers.</summary>
    [JsonIgnore] public string Display => !IsRanked ? "Unranked"
        : Tier == "Onyx" ? $"Onyx {Value}"
        : $"{Tier} {SubTier + 1}";
}

// ---- Metadata --------------------------------------------------------------------

/// <summary>GET discovery-infiniteugc.svc.halowaypoint.com/hi/{maps|ugcGameVariants|playlists}/{id}/versions/{ver}</summary>
public sealed record UgcAsset(string AssetId, string VersionId, string? PublicName, string? Description, UgcFiles? Files)
{
    [JsonIgnore] public string? ThumbnailUrl => FileUrl("images/thumbnail.jpg");
    [JsonIgnore] public string? HeroUrl => FileUrl("images/hero.jpg") ?? ThumbnailUrl;

    /// <summary>The Forge navmesh, when the map has one (343-built maps don't).</summary>
    public const string NavMeshFile = "navmesh.blob";

    /// <summary>
    /// A file the asset lists, as a URL on the public UGC blob host (which needs no token), or null
    /// when it isn't listed or the prefix isn't that host.
    /// </summary>
    public Uri? BlobFileUri(string rel) =>
        FileUrl(rel) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host == "blobs-infiniteugc.svc.halowaypoint.com" && string.IsNullOrEmpty(uri.Query)
            ? uri : null;

    private string? FileUrl(string rel) =>
        Files is { } f && f.FileRelativePaths.Contains(rel) ? f.Prefix + rel : null;
}

public sealed record UgcFiles(string Prefix, List<string> FileRelativePaths);

/// <summary>GET gamecms-hacs.svc.halowaypoint.com/hi/Waypoint/file/medals/metadata.json</summary>
public sealed record MedalMetadata(
    List<string> Difficulties,
    List<string> Types,
    Dictionary<string, MedalSprite> Sprites,
    List<MedalDefinition> Medals);

/// <summary>A sprite sheet: medal N is at column N % Columns, row N / Columns, each Size px square.</summary>
public sealed record MedalSprite(string Path, int Columns, int Size);

public sealed record MedalDefinition(
    long NameId,
    [property: JsonConverter(typeof(LocalizedStringConverter))] string? Name,
    [property: JsonConverter(typeof(LocalizedStringConverter))] string? Description,
    int SpriteIndex,
    int SortingWeight,
    int DifficultyIndex,
    int TypeIndex,
    int PersonalScore);

/// <summary>GET economy.svc.halowaypoint.com/hi/players/xuid({xuid})/customization/appearance</summary>
public sealed record AppearanceResponse(Appearance? Appearance);
public sealed record Appearance(string? ServiceTag);

// ---- Theater ---------------------------------------------------------------------

/// <summary>GET discovery-infiniteugc.svc.halowaypoint.com/hi/films/matches/{matchId}/spectate</summary>
public sealed record FilmManifest(int FilmStatusBond, FilmCustomData CustomData, string BlobStoragePathPrefix, string AssetId);

public sealed record FilmCustomData(
    long FilmLength, List<FilmChunkInfo> Chunks, bool HasGameEnded, string MatchId, int FilmMajorVersion)
{
    /// <summary>
    /// Whether the film has its summary chunk (type 3), which holds the kills, medals and scores.
    /// Matches that didn't end normally have none (their manifest says <c>HasGameEnded: false</c>).
    /// </summary>
    public bool HasSummary => Chunks.Any(c => c.ChunkType == 3);
}

/// <summary>ChunkType 1 = header, 2 = replication (about 20 s each), 3 = summary.</summary>
public sealed record FilmChunkInfo(
    int Index, long ChunkStartTimeOffsetMilliseconds, long DurationMilliseconds, long ChunkSize,
    string FileRelativePath, int ChunkType);

// ---- Helpers -----------------------------------------------------------------------

public static class IsoDuration
{
    /// <summary>Parses the API's ISO 8601 durations ("PT15M17.3S", "P9DT18H33M4.5S").</summary>
    public static TimeSpan Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return TimeSpan.Zero;
        try { return XmlConvert.ToTimeSpan(value); }
        catch (FormatException) { return TimeSpan.Zero; }
    }

    public static bool TryParse(string? value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrEmpty(value) || value[0] != 'P') return false;
        try { result = XmlConvert.ToTimeSpan(value); return true; }
        catch (FormatException) { return false; }
    }
}
