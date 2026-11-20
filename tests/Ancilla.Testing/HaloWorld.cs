using System.Text.Json;
using System.Xml;
using Ancilla.Core;
using static Ancilla.Testing.Spartans;

namespace Ancilla.Testing;

/// <summary>
/// Everything the fake Halo backend knows: Noble Six's matches, progress and medals, all made up
/// and generated the same way every time. Service records are added up from the matches, so
/// filtered and unfiltered records agree with each other the way the real ones do.
/// </summary>
public sealed class HaloWorld
{
    public static class Ids
    {
        private static string Make(uint kind, int n) => $"{kind:x8}-0000-4000-8000-{n:D12}";
        public static string Match(int n) => Make(0x000ba771, n);
        public static string Map(int n) => Make(0x0000ba5e, n);
        public static string Mode(int n) => Make(0x0000a0de, n);
        public static string Playlist(int n) => Make(0x00001157, n);
        public static string Version(int n) => Make(0x0000fe25, n);
        public const string RankedArena = "edfef3ac-9cbe-4fa2-b949-8f29deafd483";
        public const string Flight = "c0a1a2a3-0000-4000-8000-000000000117";
    }

    public sealed record Asset(string AssetId, string VersionId, string Name)
    {
        public AssetRef Ref(int kind) => new(kind, AssetId, VersionId);
        public UgcAsset Ugc => new(AssetId, VersionId, Name, $"{Name}, a synthetic test asset.", null);
    }

    // ---- Content ---------------------------------------------------------------------

    public static readonly Asset LiveFire = new(Ids.Map(1), Ids.Version(1), "Live Fire");
    public static readonly Asset Streets = new(Ids.Map(2), Ids.Version(2), "Streets");
    public static readonly Asset Aquarius = new(Ids.Map(3), Ids.Version(3), "Aquarius");
    public static readonly Asset Bazaar = new(Ids.Map(4), Ids.Version(4), "Bazaar");
    public static readonly Asset Behemoth = new(Ids.Map(5), Ids.Version(5), "Behemoth");
    public static readonly Asset Recharge = new(Ids.Map(6), Ids.Version(6), "Recharge");

    public static readonly Asset Slayer = new(Ids.Mode(1), Ids.Version(11), "Slayer");
    public static readonly Asset Oddball = new(Ids.Mode(2), Ids.Version(12), "Oddball");
    public static readonly Asset Ctf = new(Ids.Mode(3), Ids.Version(13), "Capture the Flag");
    public static readonly Asset Ffa = new(Ids.Mode(4), Ids.Version(14), "Free-for-All Slayer");
    public static readonly Asset Firefight = new(Ids.Mode(5), Ids.Version(15), "Firefight: King of the Hill");

    public static readonly Asset RankedArena = new(Ids.RankedArena, Ids.Version(21), "Ranked Arena");
    public static readonly Asset QuickPlay = new(Ids.Playlist(2), Ids.Version(22), "Quick Play");
    public static readonly Asset FreeForAll = new(Ids.Playlist(3), Ids.Version(23), "Free-for-All");
    public static readonly Asset FirefightPlaylist = new(Ids.Playlist(4), Ids.Version(24), "Firefight: Heroic");

    private sealed record Template(Asset Mode, int Category, Asset Map, Asset Playlist, bool Teams, bool Pve, string? ModeGroup);

    // Matchmade games cycle through these.
    private static readonly Template[] Templates =
    [
        new(Slayer, 6, LiveFire, RankedArena, true, false, null),
        new(Oddball, 18, Streets, QuickPlay, true, false, "OddballStats"),
        new(Ctf, 15, Aquarius, RankedArena, true, false, "CaptureTheFlagStats"),
        new(Ffa, 6, Bazaar, FreeForAll, false, false, null),
        new(Firefight, GameVariantCategories.Firefight, Behemoth, FirefightPlaylist, true, true, null),
        new(Slayer, 6, Recharge, QuickPlay, true, false, null),
    ];

    public static readonly IReadOnlyList<Asset> Maps = [LiveFire, Streets, Aquarius, Bazaar, Behemoth, Recharge];
    public static readonly IReadOnlyList<Asset> Modes = [Slayer, Oddball, Ctf, Ffa, Firefight];
    public static readonly IReadOnlyList<Asset> Playlists = [RankedArena, QuickPlay, FreeForAll, FirefightPlaylist];

    /// <summary>One game: the history entry (from Noble Six's side) and the full scoreboard.</summary>
    public sealed record Game(MatchHistoryEntry Entry, MatchStats Stats, MatchHistoryType Type, int Category, bool IsRanked, bool IsPve)
    {
        public string Id => Entry.MatchId;
        public MatchPlayer Me => Stats.Players.First(p => p.Xuid == NobleSix.Xuid);
    }

    public DateTimeOffset Now { get; }
    public List<Game> Games { get; } = [];
    public UserProfile Me => Profile(NobleSix);

    public const int MatchmadeCount = 24;
    public const int CustomCount = 2;

    /// <param name="matchmade">How many matchmade games to make. More than 200 tests going past the first pages of history.</param>
    public HaloWorld(DateTimeOffset? now = null, int matchmade = MatchmadeCount)
    {
        Now = now ?? DateTimeOffset.UtcNow;
        for (var i = 0; i < matchmade; i++) Games.Add(Matchmade(i));
        for (var i = 0; i < CustomCount; i++) Games.Add(Custom(matchmade + i, i));
    }

    public static UserProfile Profile(Spartan s) => new(s.Xuid, s.Gamertag);

    public Game GameById(string id) => Games.First(g => g.Id == id);
    public IEnumerable<Game> Matchmade() => Games.Where(g => g.Type == MatchHistoryType.Matchmaking);

    // ---- Matches -------------------------------------------------------------------

    private Game Matchmade(int i)
    {
        var t = Templates[i % Templates.Length];
        var won = i % 3 != 2;
        var start = Now.AddHours(-(1 + i * 3));
        var info = new MatchInfo(start, start.AddMinutes(11), Ids.Flight, "PT11M12.5S", t.Category,
            t.Map.Ref(2), t.Mode.Ref(6), t.Playlist.Ref(3), 3, t.Teams);

        List<(Spartan? Who, int Team)> roster;
        if (t.Pve)
            roster = [(NobleSix, 0), (Kat, 0), (Jorge, 0), (Fred, 0)];
        else if (!t.Teams)
            roster = [(NobleSix, 0), (Fred, 1), (Kelly, 2), (Linda, 3), (Thel, 4), (null, 5)];
        else
        {
            // Carter and Jun swap sides, so they're sometimes teammates and sometimes opponents.
            var (withUs, against) = i % 2 == 0 ? (Carter, Jun) : (Jun, Carter);
            roster = [(NobleSix, 0), (Kat, 0), (Emile, 0), (withUs, 0), (Thel, 1), (Rtas, 1), (HalfJaw, 1), (against, 1)];
        }

        var players = roster.Select((r, p) => Player(i, p, r.Who, r.Team, t, won)).ToList();
        List<MatchTeam> teams = !t.Teams ? [] : t.Pve
            ? [TeamOf(0, won ? MatchOutcome.Win : MatchOutcome.Loss, 1, players)]
            : [TeamOf(0, won ? MatchOutcome.Win : MatchOutcome.Loss, won ? 1 : 2, players),
               TeamOf(1, won ? MatchOutcome.Loss : MatchOutcome.Win, won ? 2 : 1, players)];
        var me = players[0];
        var entry = new MatchHistoryEntry(Ids.Match(i), info, me.LastTeamId, me.Outcome, me.Rank, true);
        return new Game(entry, new MatchStats(Ids.Match(i), info, teams, players),
            MatchHistoryType.Matchmaking, t.Category, t.Playlist == RankedArena, t.Pve);
    }

    private Game Custom(int i, int n)
    {
        var start = Now.AddDays(-2).AddHours(-n);
        var info = new MatchInfo(start, start.AddMinutes(8), Ids.Flight, "PT8M", 6, Bazaar.Ref(2), Slayer.Ref(6), null, 1, true);
        var t = new Template(Slayer, 6, Bazaar, QuickPlay, true, false, null);
        var players = new (Spartan, int)[] { (NobleSix, 0), (Fred, 0), (Kelly, 1), (Linda, 1) }
            .Select((r, p) => Player(i, p, r.Item1, r.Item2, t, true)).ToList();
        var teams = new List<MatchTeam> { TeamOf(0, MatchOutcome.Win, 1, players), TeamOf(1, MatchOutcome.Loss, 2, players) };
        var entry = new MatchHistoryEntry(Ids.Match(i), info, 0, MatchOutcome.Win, 1, true);
        return new Game(entry, new MatchStats(Ids.Match(i), info, teams, players), MatchHistoryType.Custom, 6, false, false);
    }

    private static MatchTeam TeamOf(int id, MatchOutcome outcome, int rank, List<MatchPlayer> players) =>
        new(id, outcome, rank, new StatsBlock
        {
            CoreStats = new CoreStats
            {
                Score = players.Where(p => p.LastTeamId == id).Sum(p => p.Stats!.CoreStats.Kills),
                Kills = players.Where(p => p.LastTeamId == id).Sum(p => p.Stats!.CoreStats.Kills),
            },
        });

    private static MatchPlayer Player(int i, int p, Spartan? who, int team, Template t, bool won)
    {
        var kills = t.Pve ? 40 + (i * 7 + p * 11) % 30 : 5 + (i * 3 + p * 5) % 15;
        var deaths = t.Pve ? 2 + (i + p) % 6 : 4 + (i * 5 + p * 3) % 12;
        var assists = (i + p * 2) % 9;
        var fired = 300 + (i * 37 + p * 53) % 400;
        var hit = fired * (40 + (i + p) % 15) / 100;
        var medals = new List<MedalCount>();
        if (kills >= 10) medals.Add(new MedalCount(Medals.DoubleKill, 1 + kills / 10, 50));
        if (kills >= 15) medals.Add(new MedalCount(Medals.KillingSpree, 1, 100));
        if (who == NobleSix && i % 4 == 0) medals.Add(new MedalCount(Medals.Perfect, 1, 0));
        if (who == NobleSix && i == 5) medals.Add(new MedalCount(Medals.UnknownNameId, 1, 0));

        var core = new CoreStats
        {
            Score = kills * 100 + assists * 50,
            PersonalScore = kills * 100 + assists * 50,
            Kills = kills,
            Deaths = deaths,
            Assists = assists,
            KDA = kills + assists / 3.0 - deaths,
            AverageLifeDuration = "PT" + (20 + (i + p) % 40) + "S",
            HeadshotKills = kills / 2,
            MeleeKills = kills / 5,
            GrenadeKills = kills / 7,
            PowerWeaponKills = kills / 6,
            ShotsFired = fired,
            ShotsHit = hit,
            Accuracy = 100.0 * hit / fired,
            DamageDealt = kills * 190 + assists * 70,
            DamageTaken = deaths * 210,
            MaxKillingSpree = Math.Max(1, kills / 4),
            Spawns = deaths + 1,
            Medals = medals,
        };

        var groups = new Dictionary<string, JsonElement>();
        if (t.Pve)
            groups["PveStats"] = Json(new { Kills = kills, Deaths = deaths, Assists = assists, BossKills = i % 3, EliteKills = kills / 4 });
        else
            groups["PvpStats"] = Json(new { Kills = kills, Deaths = deaths, Assists = assists, KDA = core.KDA });
        if (t.ModeGroup == "OddballStats")
            groups["OddballStats"] = Json(new
            {
                KillsAsSkullCarrier = p % 3, LongestTimeAsSkullCarrier = $"PT{30 + p}.5S", SkullCarriersKilled = (i + p) % 4,
                SkullGrabs = 1 + p % 3, SkullScoringTicks = 20 + i + p, TimeAsSkullCarrier = $"PT1M{10 + p}S",
            });
        if (t.ModeGroup == "CaptureTheFlagStats")
            groups["CaptureTheFlagStats"] = Json(new
            {
                FlagCaptures = p % 2, FlagReturns = (i + p) % 3, FlagSecures = p % 2, FlagSteals = (i + p) % 2, TimeAsFlagCarrier = $"PT{15 + p}S",
            });

        var outcome = !t.Teams ? (p == 0 && i % 3 != 2 ? MatchOutcome.Win : MatchOutcome.Loss)
            : (team == 0) == won ? MatchOutcome.Win : MatchOutcome.Loss;
        var rank = !t.Teams ? (p == 0 && i % 3 != 2 ? 1 : p + 1) : outcome == MatchOutcome.Win ? 1 : 2;
        var id = who is null ? $"bid({2000 + p}.0)" : $"xuid({who.Xuid})";
        return new MatchPlayer(id, who is null ? 2 : 1, team, outcome, rank,
            new ParticipationInfo(i % 7 == 3 && p == 2, false, true, "PT11M"),
            [new PlayerTeamStats(team, new StatsBlock { CoreStats = core, ModeStats = groups })]);
    }

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);

    // ---- Service records ---------------------------------------------------------------

    /// <summary>Noble Six's matchmade service record, narrowed like the real endpoint's filters.</summary>
    public ServiceRecord ServiceRecord(int? category = null, bool? isRanked = null)
    {
        var games = Matchmade()
            .Where(g => category is null || g.Category == category)
            .Where(g => isRanked is null || g.IsRanked == isRanked)
            .ToList();
        var mine = games.Select(g => g.Me.Stats!).ToList();
        var cores = mine.Select(s => s.CoreStats).ToList();
        int Sum(Func<CoreStats, int> f) => cores.Sum(f);
        long SumL(Func<CoreStats, long> f) => cores.Sum(f);
        var kills = Sum(c => c.Kills);
        var deaths = Sum(c => c.Deaths);
        var assists = Sum(c => c.Assists);
        var fired = SumL(c => c.ShotsFired);
        var hit = SumL(c => c.ShotsHit);

        var time = games.Aggregate(TimeSpan.Zero, (t, g) => t + IsoDuration.Parse(g.Stats.MatchInfo.Duration));
        return new ServiceRecord
        {
            TimePlayed = XmlConvert.ToString(time),
            MatchesCompleted = games.Count,
            Wins = games.Count(g => g.Me.Outcome == MatchOutcome.Win),
            Losses = games.Count(g => g.Me.Outcome == MatchOutcome.Loss),
            Ties = 0,
            CoreStats = new CoreStats
            {
                Score = SumL(c => c.Score),
                PersonalScore = SumL(c => c.PersonalScore),
                Kills = kills,
                Deaths = deaths,
                Assists = assists,
                AverageKDA = games.Count == 0 ? 0 : (kills + assists / 3.0 - deaths) / games.Count,
                HeadshotKills = Sum(c => c.HeadshotKills),
                MeleeKills = Sum(c => c.MeleeKills),
                GrenadeKills = Sum(c => c.GrenadeKills),
                PowerWeaponKills = Sum(c => c.PowerWeaponKills),
                ShotsFired = fired,
                ShotsHit = hit,
                Accuracy = fired == 0 ? 0 : 100.0 * hit / fired,
                DamageDealt = SumL(c => c.DamageDealt),
                DamageTaken = SumL(c => c.DamageTaken),
                MaxKillingSpree = cores.Count == 0 ? 0 : cores.Max(c => c.MaxKillingSpree),
                Spawns = Sum(c => c.Spawns),
                Medals = cores.SelectMany(c => c.Medals).GroupBy(m => m.NameId)
                    .Select(g => new MedalCount(g.Key, g.Sum(m => m.Count), g.Sum(m => m.TotalPersonalScoreAwarded))).ToList(),
            },
            Subqueries = new ServiceRecordSubqueries(games.Select(g => g.Stats.MatchInfo.Playlist!.AssetId).Distinct().ToList()),
            ModeStats = SumGroups(mine.Select(s => s.ModeStats)),
        };
    }

    /// <summary>Adds up per-mode groups field by field: counts and durations add, best values take the maximum.</summary>
    private static Dictionary<string, JsonElement> SumGroups(IEnumerable<Dictionary<string, JsonElement>> all)
    {
        var sums = new Dictionary<string, Dictionary<string, object>>();
        foreach (var groups in all)
        foreach (var (key, group) in groups)
        {
            if (!sums.TryGetValue(key, out var acc)) sums[key] = acc = [];
            foreach (var f in group.EnumerateObject())
            {
                acc.TryGetValue(f.Name, out var prev);
                var max = GameVariantCategories.IsMaximum(f.Name);
                acc[f.Name] = f.Value.ValueKind switch
                {
                    JsonValueKind.Number when f.Value.TryGetInt64(out var l) && prev is null or long =>
                        max ? Math.Max(l, (long)(prev ?? 0L)) : l + (long)(prev ?? 0L),
                    JsonValueKind.Number => max
                        ? Math.Max(f.Value.GetDouble(), Convert.ToDouble(prev ?? 0.0))
                        : f.Value.GetDouble() + Convert.ToDouble(prev ?? 0.0),
                    JsonValueKind.String => Duration(f.Name, f.Value.GetString(), prev as string),
                    _ => f.Value.Clone(),
                };
            }
        }
        return sums.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));

        static string Duration(string name, string? value, string? prev)
        {
            var a = IsoDuration.Parse(value);
            var b = IsoDuration.Parse(prev);
            return XmlConvert.ToString(GameVariantCategories.IsMaximum(name) ? (a > b ? a : b) : a + b);
        }
    }

    // ---- Skill -------------------------------------------------------------------------

    public static Csr CsrOf(int value)
    {
        if (value < 0) return new Csr(-1, 5, "", 0, 0, "", 0, 0, 5);
        (string Tier, int Start)[] tiers = [("Bronze", 0), ("Silver", 300), ("Gold", 600), ("Platinum", 900), ("Diamond", 1200), ("Onyx", 1500)];
        var (tier, start) = tiers.Last(t => value >= t.Start);
        if (tier == "Onyx") return new Csr(value, 0, "Onyx", 1500, 0, "Onyx", 1500, 0, 5);
        var sub = Math.Min(5, (value - start) / 50);
        return new Csr(value, 0, tier, start + sub * 50, sub, sub == 5 ? "Onyx" : tier, start + (sub + 1) * 50, sub == 5 ? 0 : sub + 1, 5);
    }

    /// <summary>Skill for Noble Six in one match, or null where the real service has none (custom games, Firefight).</summary>
    public MatchSkill? Skill(string matchId)
    {
        var g = Games.FirstOrDefault(x => x.Id == matchId);
        if (g is null || g.Type != MatchHistoryType.Matchmaking || g.IsPve) return null;
        var i = Games.IndexOf(g);
        var me = g.Me.Stats!.CoreStats;
        var perf = new Dictionary<string, StatPerformance>
        {
            ["Kills"] = new(me.Kills, me.Kills - 1.5 + i % 4, 3.2),
            ["Deaths"] = new(me.Deaths, me.Deaths + 0.5 - i % 3, 2.8),
        };
        var pre = 1400 + i * 8;
        var post = pre + (g.Me.Outcome == MatchOutcome.Win ? 9 : -7);
        var recap = g.IsRanked ? new RankRecap(CsrOf(pre), CsrOf(post)) : new RankRecap(CsrOf(-1), CsrOf(-1));
        var mmrs = g.Stats.MatchInfo.TeamsEnabled ? new Dictionary<string, double> { ["0"] = 1450 + i, ["1"] = 1440 - i } : null;
        return new MatchSkill(1450 + i, recap, perf, g.Me.LastTeamId, mmrs);
    }

    public static PlaylistCsr RankedArenaCsr => new(CsrOf(1452), CsrOf(1490), CsrOf(1530));

    // ---- Progression -------------------------------------------------------------------

    public const string CareerPath = "RewardTracks/CareerRanks/careerRank1.json";
    public const string ActivePassPath = "RewardTracks/Operations/OpFallOfReach.json";
    public const string FinishedPassPath = "RewardTracks/Operations/OpSacredIcon.json";
    public const string UnownedPassPath = "RewardTracks/Operations/OpFirstStrike.json";
    /// <summary>A pass whose track file can't be fetched; the progress report skips it.</summary>
    public const string MissingPassPath = "RewardTracks/Operations/OpLegacyMissing.json";

    public const int CareerRanks = 20;

    public static RewardTrackDefinition CareerDefinition()
    {
        string[] titles = ["Recruit", "Cadet", "Private", "Lance Corporal", "Corporal", "Sergeant", "Staff Sergeant"];
        var ranks = Enumerable.Range(1, CareerRanks).Select(r => r == CareerRanks
            ? new RankDefinition(r, 5000, "Hero", null, null, "career_rank/ProgressWidget/272_Hero.png")
            : new RankDefinition(r, 500 + r * 250, titles[(r - 1) / 3 % titles.Length], r % 2 == 0 ? "Silver" : "Bronze",
                ((r - 1) % 3 + 1).ToString(), $"career_rank/ProgressWidget/{r}_Rank.png")).ToList();
        return new RewardTrackDefinition("careerRank1", "Career Rank", 1000, ranks);
    }

    public static RewardTrackProgress CareerProgress() =>
        new(CareerPath, "CareerRank", new TrackProgressState(12, 400, true, false), true);

    public static RewardTrackDefinition PassDefinition(string path, string name, int ranks) =>
        new(Path.GetFileNameWithoutExtension(path), name, 1000,
            Enumerable.Range(1, ranks).Select(r => new RankDefinition(r, null, null, null, null)).ToList());

    public static OperationsResponse Operations() => new(ActivePassPath,
    [
        new(ActivePassPath, "Operation", new TrackProgressState(7, 300, true, false), true),
        new(FinishedPassPath, "Operation", new TrackProgressState(20, 0, true, true), true),
        new(UnownedPassPath, "Operation", new TrackProgressState(0, 0, false, false), false),
        new(MissingPassPath, "Operation", new TrackProgressState(3, 0, true, false), true),
    ]);

    public static RewardTrackDefinition? Definition(string path) => path switch
    {
        CareerPath => CareerDefinition(),
        ActivePassPath => PassDefinition(path, "Operation: Fall of Reach", 30),
        FinishedPassPath => PassDefinition(path, "Operation: Sacred Icon", 20),
        UnownedPassPath => PassDefinition(path, "Operation: First Strike", 25),
        _ => null,
    };

    // ---- Medals --------------------------------------------------------------------------

    public static class Medals
    {
        public const long DoubleKill = 1_000_001;
        public const long KillingSpree = 1_000_002;
        public const long Perfect = 1_000_003;
        public const long Killjoy = 1_000_004;
        public const long Overkill = 1_000_005;
        /// <summary>A medal the metadata doesn't list, as happens after a game update.</summary>
        public const long UnknownNameId = 4_000_000_001;

        public const string SheetPath = "medals/images/medal_sheet_med.png";

        public static MedalMetadata Metadata() => new(
            ["normal", "heroic", "legendary", "mythic"],
            ["spree", "mode", "multi-kill", "proficiency", "skill", "style"],
            new Dictionary<string, MedalSprite>
            {
                ["small"] = new("medals/images/medal_sheet_sm.png", 16, 72),
                ["medium"] = new(SheetPath, 16, 128),
            },
            [
                new(DoubleKill, "Double Kill", "Kill 2 enemies within a short time.", 0, 10, 0, 2, 50),
                new(KillingSpree, "Killing Spree", "Kill 5 enemies without dying.", 1, 20, 1, 0, 100),
                new(Perfect, "Perfect", "Kill an enemy with only precision headshots.", 2, 30, 1, 4, 0),
                new(Killjoy, "Killjoy", "End an enemy's killing spree.", 3, 40, 0, 0, 0),
                new(Overkill, "Overkill", "Kill 4 enemies within a short time.", 4, 50, 2, 2, 150),
            ]);
    }

    /// <summary>A 1×1 transparent PNG, for every image the fake CMS serves.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
}
