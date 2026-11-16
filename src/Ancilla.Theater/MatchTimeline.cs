using Ancilla.Core;
using Ancilla.Theater.Format;

namespace Ancilla.Theater;

/// <summary>A decoded player: roster players in scoreboard order, then late joiners.</summary>
public sealed record TimelinePlayer(int Index, int Team, string Gamertag, string? Xuid, bool LateJoiner);

public enum FeedKind { Kill, Death, Medal, Objective }

/// <summary>
/// One kill-feed line. <see cref="Kind"/> Kill: <see cref="Player"/> killed <see cref="Other"/> (null
/// when the victim is a bot or another player with no records). Death: <see cref="Player"/> died with
/// no killer recorded (a suicide, or killed by a bot). Medal: a medal not tied to a kill. Objective:
/// a mode-objective event (in CTF, a flag action). <see cref="MedalNameIds"/> are the medals a kill
/// earned, or the one medal of a Medal line; medals whose code isn't known yet are left out.
/// </summary>
public sealed record FeedEntry(uint TimeMs, FeedKind Kind, int Player, int? Other, IReadOnlyList<long> MedalNameIds);

/// <summary>
/// Per-player figures from the film. LongestStreak is kills in one life. Traded: kills after which
/// the killer died within <see cref="MatchTimeline.TradeWindowMs"/>. Revenge: kills of the player's
/// last killer. KillDistance is the median, in film units, or null when no kill could be placed.
/// </summary>
public sealed record PlayerSummary(int Player, int Kills, int Deaths, int LongestStreak, uint? FirstKillMs, uint? FirstDeathMs,
    int Traded, int Revenge, int Objectives, int? Nemesis, int LivesPlaced, double DistanceMoved, double? KillDistance);

/// <summary>Where a player died, in map cells (0 ≤ X &lt; MapWidth, 0 ≤ Y &lt; MapHeight, Y down).</summary>
public sealed record DeathPoint(uint TimeMs, int Player, int Team, int? Killer, int X, int Y);

/// <summary>
/// A match's timeline from its Theater film: the kill feed, per-player figures, team kills over
/// time, head-to-head kills and, for PvP, where players died. Built from a cached film (see
/// <see cref="FilmCache"/>) and the match's scoreboard. Positions are in film units (scale and axes
/// unknown), so the map is drawn on a grid scaled to the area players covered.
/// </summary>
public sealed record MatchTimeline(
    IReadOnlyList<TimelinePlayer> Players,
    IReadOnlyList<int> Teams,
    uint DurationMs,
    IReadOnlyList<FeedEntry> Feed,
    IReadOnlyList<PlayerSummary> Summaries,
    IReadOnlyList<int[]> TeamKillsPerMinute,
    int[,] HeadToHead,
    IReadOnlyList<DeathPoint> Deaths,
    IReadOnlySet<(int X, int Y)> Arena,
    int BotsAndUnlinked)
{
    public const int TradeWindowMs = 3_000;
    public const int MapWidth = 120, MapHeight = 80;
    private const int FirefightCategory = 42;

    public bool HasPositions => Arena.Count > 0;

    public static MatchTimeline Build(FilmCache cache, string matchId, MatchStats board, bool positions = true)
    {
        var header = cache.LoadChunk(matchId, FilmCache.ChunkOfType(cache.LoadManifest(matchId), 1));
        var named = Roster.Find(header, FilmHeader.Parse(header).StreamOffset).Where(r => r.Gamertag is not null).ToList();
        var summary = PacketReader.Read(cache.LoadChunk(matchId, FilmCache.ChunkOfType(cache.LoadManifest(matchId), 3)), out _);
        var walked = SummaryRecords.Walk(Summary.Payload(summary)).Records;
        var (resolved, _) = FilmPlayers.Resolve(named, walked, board);
        if (resolved.Count == 0) throw new InvalidDataException("No players could be decoded from this film.");
        var events = Summary.FromRecords(walked, resolved.Select(x => (string?)x.Gamertag).ToList());
        Summary.PairKills(events);

        var players = resolved.Select((x, i) => new TimelinePlayer(i, x.Row.LastTeamId, x.Gamertag, x.Row.Xuid, x.LateJoiner)).ToList();
        var teams = players.Select(p => p.Team).Distinct().Order().ToList();
        var outsiders = board.Players.Count - resolved.Count;
        var duration = (uint)Math.Max(events.Count > 0 ? events.Max(e => e.TimeMs) : 0,
            board.MatchInfo.Duration is { } d ? IsoDuration.Parse(d).TotalMilliseconds : 0);

        // ---- Kill feed ----
        var medalOf = Timeline.AssignMedals(events);
        var medalsByKill = medalOf.GroupBy(x => x.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Key).ToList());
        long? NameId(SummaryEvent e) => MedalCodes.Known.TryGetValue(e.MedalCode, out var id) ? id : null;
        var feed = new List<FeedEntry>();
        var killerOf = new Dictionary<int, int>();   // death event index → killer
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            switch (e.Kind)
            {
                case SummaryEventKind.PlayerKill:
                    var medals = medalsByKill.GetValueOrDefault(i)?.Select(j => NameId(events[j])).OfType<long>().ToList() ?? [];
                    feed.Add(new FeedEntry(e.TimeMs, FeedKind.Kill, e.Player, e.Victim, medals));
                    if (e.Victim is not null) killerOf[i + 1] = e.Player;
                    break;
                case SummaryEventKind.Death when !killerOf.ContainsKey(i):
                    feed.Add(new FeedEntry(e.TimeMs, FeedKind.Death, e.Player, null, []));
                    break;
                case SummaryEventKind.Medal when !medalOf.ContainsKey(i) && NameId(e) is { } id:
                    feed.Add(new FeedEntry(e.TimeMs, FeedKind.Medal, e.Player, null, [id]));
                    break;
                case SummaryEventKind.Objective:
                    feed.Add(new FeedEntry(e.TimeMs, FeedKind.Objective, e.Player, null, []));
                    break;
            }
        }

        // ---- Team kills per minute and head-to-head ----
        var minutes = (int)(duration / 60_000) + 1;
        var perMinute = Enumerable.Range(0, minutes).Select(_ => new int[teams.Count]).ToList();
        var h2h = new int[players.Count, players.Count];
        foreach (var k in events.Where(e => e.Kind == SummaryEventKind.PlayerKill))
        {
            perMinute[Math.Min(minutes - 1, (int)(k.TimeMs / 60_000))][teams.IndexOf(players[k.Player].Team)]++;
            if (k.Victim is { } v) h2h[k.Player, v]++;
        }

        // ---- Positions (PvP only; in Firefight the AI share the biped slots) ----
        var deathIdx = events.Select((e, i) => (e, i)).Where(x => x.e.Kind == SummaryEventKind.Death).Select(x => x.i).ToList();
        var lives = new Dictionary<int, List<int>>();
        var deathPoints = new List<DeathPoint>();
        var arena = new HashSet<(int, int)>();
        Dictionary<int, BipedTrack> tracks = [];
        var killDistances = new Dictionary<int, List<double>>();
        if (positions && board.MatchInfo.GameVariantCategory != FirefightCategory && deathIdx.Count > 0)
        {
            var pos = FilmPositions.Load(cache, matchId);
            tracks = Lives.Tracks(pos.Bipeds);
            if (tracks.Count >= 8)
            {
                var deaths = deathIdx.Select(i => events[i]).ToList();
                var deathTicks = deaths.Select(x => pos.TickAt(x.TimeMs)).ToList();
                var matched = Lives.MatchDeaths(deathTicks, tracks);
                var respawns = Lives.Respawns(deaths.Select(x => (double)x.TimeMs).ToList(), matched.Keys, tracks, pos.TickMs);
                lives = Lives.Chain(deaths.Select(x => x.Player).ToList(), matched, respawns, tracks);

                var all = pos.Bipeds.Select(b => b.Record.Position).ToList();
                int minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
                // One scale for both axes, so the map keeps its shape; centred in the grid.
                var scale = Math.Min((MapWidth - 1.0) / Math.Max(1, maxX - minX), (MapHeight - 1.0) / Math.Max(1, maxY - minY));
                var (padX, padY) = ((MapWidth - 1 - (maxX - minX) * scale) / 2, (MapHeight - 1 - (maxY - minY) * scale) / 2);
                (int, int) Cell(FilmPosition p) => ((int)(padX + (p.X - minX) * scale), (int)(padY + (maxY - p.Y) * scale));
                foreach (var p in all) arena.Add(Cell(p));

                FilmPosition? At(int player, int tick)
                {
                    foreach (var slot in lives.GetValueOrDefault(player) ?? [])
                    {
                        var t = tracks[slot];
                        if (tick < t.FirstTick || tick > t.LastTick + 3) continue;
                        var i = t.Points.FindLastIndex(x => x.Tick <= tick);
                        return i >= 0 ? t.Points[i].Position : null;
                    }
                    return null;
                }
                foreach (var (n, slot) in matched)
                {
                    var at = tracks[slot].Points[^1].Position;
                    var killer = killerOf.TryGetValue(deathIdx[n], out var k) ? k : (int?)null;
                    var (x, y) = Cell(at);
                    deathPoints.Add(new DeathPoint(deaths[n].TimeMs, deaths[n].Player, players[deaths[n].Player].Team, killer, x, y));
                    if (killer is { } kp && At(kp, deathTicks[n]) is { } killerAt)
                        (killDistances.TryGetValue(kp, out var l) ? l : killDistances[kp] = []).Add(at.HorizontalDistance(killerAt));
                }
                deathPoints.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            }
        }

        // ---- Per player ----
        var summaries = new List<PlayerSummary>();
        foreach (var p in players)
        {
            var mine = events.Select((e, i) => (e, i)).Where(x => x.e.Player == p.Index).ToList();
            var kills = mine.Where(x => x.e.Kind == SummaryEventKind.PlayerKill).Select(x => x.e).ToList();
            var deaths = mine.Where(x => x.e.Kind == SummaryEventKind.Death).ToList();
            int streak = 0, best = 0, revenge = 0; int? lastKiller = null;
            foreach (var (e, i) in mine)
            {
                if (e.Kind == SummaryEventKind.PlayerKill)
                {
                    best = Math.Max(best, ++streak);
                    if (lastKiller is not null && e.Victim == lastKiller) { revenge++; lastKiller = null; }
                }
                else if (e.Kind == SummaryEventKind.Death) { streak = 0; lastKiller = killerOf.TryGetValue(i, out var k) ? k : null; }
            }
            var traded = kills.Count(k => deaths.Any(x => x.e.TimeMs >= k.TimeMs && x.e.TimeMs - k.TimeMs <= TradeWindowMs));
            var nemesis = deaths.Select(x => killerOf.TryGetValue(x.i, out var k) ? k : -1).Where(k => k >= 0)
                .GroupBy(k => k).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (int?)g.Key).FirstOrDefault();
            var myLives = lives.GetValueOrDefault(p.Index) ?? [];
            var moved = myLives.Sum(s => tracks[s].Points.Zip(tracks[s].Points.Skip(1)).Sum(x => x.First.Position.HorizontalDistance(x.Second.Position)));
            var kd = killDistances.GetValueOrDefault(p.Index)?.Order().ToList();
            summaries.Add(new PlayerSummary(p.Index, kills.Count, deaths.Count, best, kills.FirstOrDefault()?.TimeMs,
                deaths.Count > 0 ? deaths[0].e.TimeMs : null, traded, revenge, mine.Count(x => x.e.Kind == SummaryEventKind.Objective),
                nemesis, myLives.Count, moved, kd is { Count: > 0 } ? kd[kd.Count / 2] : null));
        }

        return new MatchTimeline(players, teams, duration, feed, summaries, perMinute, h2h, deathPoints, arena, outsiders);
    }
}
