using Ancilla.Core;
using Ancilla.Theater.Format;

namespace Ancilla.Theater;

/// <summary>
/// <c>Ancilla.Theater timeline</c>: a match timeline and head-to-head analysis built from the summary
/// chunk. A prototype for an app feature. Players are labelled "T{team} P{roster index}" unless
/// gamertags are asked for.
/// </summary>
public static class Timeline
{
    // Estimates, from five CTF films (see README): for 35 full-match players, the time the
    // scoreboard doesn't count as alive (timePlayed − averageLifeDuration × spawns) fits
    // 8.0 s per death + 38 s, and no player has an event before 35 s on the event clock.
    public const int RespawnEstimateMs = 8_000;
    public const int FirstSpawnEstimateMs = 35_000;

    /// <summary>A kill counts as traded when its killer dies within this long after it.</summary>
    public const int TradeWindowMs = 3_000;

    // Kill medals are recorded from 2 ms before their kill (Killjoy, Reversal, Whiplash) to about
    // 280 ms after it (Bulltrue), across five PvP films.
    public const int MedalBeforeMs = 5, MedalAfterMs = 500;

    /// <summary>
    /// Which kill each medal belongs to, as medal event index → kill event index: the same
    /// player's kill nearest in time, from <see cref="MedalBeforeMs"/> before the medal's kill to
    /// <see cref="MedalAfterMs"/> after it. On a tie, the kill that came first wins. Medals with no
    /// kill in range (Odin's Raven, Steaktacular, Wingman, ...) aren't in the result.
    /// </summary>
    public static Dictionary<int, int> AssignMedals(IReadOnlyList<SummaryEvent> events)
    {
        var result = new Dictionary<int, int>();
        for (var j = 0; j < events.Count; j++)
        {
            if (events[j].Kind != SummaryEventKind.Medal) continue;
            int? best = null; var bestGap = long.MaxValue;
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i].Kind != SummaryEventKind.PlayerKill || events[i].Player != events[j].Player) continue;
                var after = (long)events[j].TimeMs - events[i].TimeMs;   // medal time minus kill time
                if (after is < -MedalBeforeMs or > MedalAfterMs) continue;
                if (Math.Abs(after) < bestGap) { best = i; bestGap = Math.Abs(after); }
            }
            if (best is { } k) result[j] = k;
        }
        return result;
    }

    private sealed record Player(int Index, int Team, string Label, MatchPlayer Row);

    public static int Run(FilmCache cache, string matchId, bool showNames, bool feed, TextWriter o)
    {
        var m = cache.LoadManifest(matchId);
        var board = cache.LoadScoreboard(matchId) ?? throw new InvalidDataException("No scoreboard cached for this film.");
        var chunks = m.CustomData.Chunks;
        var header = cache.LoadChunk(matchId, FilmCache.ChunkOfType(m, 1));
        var named = Roster.Find(header, FilmHeader.Parse(header).StreamOffset).Where(r => r.Gamertag is not null).ToList();
        var summary = PacketReader.Read(cache.LoadChunk(matchId, FilmCache.ChunkOfType(m, 3)), out _);
        var walked = SummaryRecords.Walk(Summary.Payload(summary)).Records;

        // Roster players, then late joiners linked to the scoreboard by their counts (see FilmPlayers).
        var (resolved, unlinked) = FilmPlayers.Resolve(named, walked, board);
        if (resolved.Count(x => !x.LateJoiner) != named.Count)
            throw new InvalidDataException("A roster player isn't on the scoreboard. Run 'Ancilla.Theater inspect'.");
        var events = Summary.FromRecords(walked, resolved.Select(x => (string?)x.Gamertag).ToList());
        Summary.PairKills(events);

        if (resolved.Count == 0)
        {
            o.WriteLine("No players could be decoded from this film; run 'Ancilla.Theater inspect' to see why.");
            return 1;
        }
        var players = resolved.Select((x, i) => new Player(i, x.Row.LastTeamId,
            $"T{x.Row.LastTeamId} P{i}" + (x.LateJoiner ? "*" : "") + (showNames ? $" {x.Gamertag}" : ""), x.Row)).ToList();
        var outsiders = board.Players.Except(resolved.Select(x => x.Row)).ToList();

        var medalNames = cache.LoadMedals()?.Medals.ToDictionary(x => x.NameId, x => x.Name ?? x.NameId.ToString()) ?? [];
        string MedalName(int code) =>
            MedalCodes.Known.TryGetValue(code, out var id) && medalNames.TryGetValue(id, out var n) ? n : $"medal code {code}";

        var durationMs = board.MatchInfo.Duration is { } d ? Ms(d) : m.CustomData.FilmLength;
        var w = players.Max(p => p.Label.Length);
        string L(Player p) => p.Label.PadRight(w);

        // A death is paired when the record before it is a kill whose victim it is (see Summary.PairKills).
        var pairedDeath = new HashSet<int>();
        var killerOf = new Dictionary<int, int>();   // death event index → killer roster index
        for (var i = 0; i + 1 < events.Count; i++)
            if (events[i].Kind == SummaryEventKind.PlayerKill && events[i].Victim is not null)
            {
                pairedDeath.Add(i + 1);
                killerOf[i + 1] = events[i].Player;
            }

        // ---- Overview ----
        var modes = board.Teams.FirstOrDefault()?.Stats.ModeStats.Keys.Where(k => k != "PvpStats").Select(k => k.Replace("Stats", "")) ?? [];
        o.WriteLine("Match timeline (times are on the summary's event clock; see Calibration)");
        o.WriteLine($"  mode category {board.MatchInfo.GameVariantCategory} ({string.Join(", ", modes)}), match {Fmt(durationMs)}, film {Fmt(m.CustomData.FilmLength)}");
        o.WriteLine($"  players: {players.Count} ({players.Count - named.Count} late joiner(s), marked *, linked by their kills, deaths and medals)");
        o.WriteLine($"  not decodable: {outsiders.Count(x => !x.IsHuman)} bot(s), {outsiders.Count(x => x.IsHuman)} other player(s) with no records, " +
                    $"{unlinked.Count} unlinked gamertag(s). Their kills and deaths show only as unmatched events.");
        o.WriteLine($"  events: {events.Count(e => e.Kind == SummaryEventKind.PlayerKill)} kills, {events.Count(e => e.Kind == SummaryEventKind.Death)} deaths, " +
                    $"{events.Count(e => e.Kind == SummaryEventKind.Medal)} medals; {events.Count(e => e.Kind == SummaryEventKind.Other)} with unknown codes (not shown)");

        // ---- Kill feed ----
        if (feed)
        {
            o.WriteLine("\nKill feed");
            var medalOf = AssignMedals(events);
            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e.Kind == SummaryEventKind.PlayerKill)
                {
                    var medals = medalOf.Where(x => x.Value == i).Select(x => x.Key).Order()
                        .Select(j => MedalName(events[j].MedalCode)).ToList();
                    var victim = e.Victim is { } v ? players[v].Label : "bot or late joiner";
                    o.WriteLine($"  {Fmt(e.TimeMs)}  {L(players[e.Player])} → {victim}{(medals.Count > 0 ? "   [" + string.Join(", ", medals) + "]" : "")}");
                }
                else if (e.Kind == SummaryEventKind.Death && !pairedDeath.Contains(i))
                    o.WriteLine($"  {Fmt(e.TimeMs)}  {L(players[e.Player])} died: suicide, or killed by a bot or late joiner");
            }
            for (var j = 0; j < events.Count; j++)
                if (events[j].Kind == SummaryEventKind.Medal && !medalOf.ContainsKey(j))
                    o.WriteLine($"  {Fmt(events[j].TimeMs)}  {L(players[events[j].Player])} medal: {MedalName(events[j].MedalCode)}  (not at a kill)");
        }

        // ---- Score flow ----
        o.WriteLine("\nScore flow (team kills)");
        var teams = players.Select(p => p.Team).Distinct().Order().ToList();
        var kills = events.Where(e => e.Kind == SummaryEventKind.PlayerKill).ToList();
        if (teams.Count == 2 && kills.Count > 0)
        {
            int a = teams[0], b = teams[1];
            int ka = 0, kb = 0; int? leader = null;
            var changes = new List<string>();
            (int Lead, uint T) bestA = (0, 0), bestB = (0, 0);
            var nextMinute = 60_000L;
            o.WriteLine($"  time    T{a}  T{b}  lead");
            void Row(long t) => o.WriteLine($"  {Fmt(t)[..5]}  {ka,3} {kb,3}  {(ka == kb ? "level" : ka > kb ? $"T{a} +{ka - kb}" : $"T{b} +{kb - ka}")}");
            foreach (var k in kills)
            {
                while (k.TimeMs >= nextMinute) { Row(nextMinute); nextMinute += 60_000; }
                if (players[k.Player].Team == a) ka++; else kb++;
                if (ka - kb > bestA.Lead) bestA = (ka - kb, k.TimeMs);
                if (kb - ka > bestB.Lead) bestB = (kb - ka, k.TimeMs);
                int? now = ka == kb ? null : ka > kb ? a : b;
                if (now is not null && leader is not null && now != leader) changes.Add($"T{now} at {Fmt(k.TimeMs)}");
                if (now is not null) leader = now;
            }
            Row(Math.Max(durationMs, kills[^1].TimeMs));
            o.WriteLine($"  lead changes: {changes.Count}{(changes.Count > 0 ? " (" + string.Join(", ", changes) + ")" : "")}");
            string Lead(int team, (int Lead, uint T) x) => x.Lead > 0 ? $"T{team} +{x.Lead} at {Fmt(x.T)}" : $"T{team} never led";
            o.WriteLine($"  largest lead: {Lead(a, bestA)}, {Lead(b, bestB)}");
            foreach (var t in teams)
            {
                var film = t == a ? ka : kb;
                var extra = outsiders.Where(x => x.LastTeamId == t).Sum(x => x.Stats?.CoreStats.Kills ?? 0);
                var sb = board.Teams.FirstOrDefault(x => x.TeamId == t)?.Stats.CoreStats;
                var check = sb is null ? "" : film + extra == sb.Kills ? "  [matches]" : "  [DIFFERS]";
                o.WriteLine($"  T{t}: {film} kills in the film + {extra} by bots and late joiners = {film + extra}; scoreboard team kills {sb?.Kills}{check}");
            }
            o.WriteLine($"  team score (scoreboard): {string.Join(", ", board.Teams.Select(t => $"T{t.TeamId} {t.Stats.CoreStats.Score}"))}" +
                        (modes.Contains("CaptureTheFlag") ? " (flag captures; capture times aren't decoded)" : ""));
        }
        else o.WriteLine("  needs two teams with player kills (Firefight films have none)");

        // ---- Head-to-head ----
        if (kills.Count > 0)
        {
            o.WriteLine("\nHead-to-head (kills by the row player on the column player; · = same team)");
            o.WriteLine($"  {"".PadRight(w)}  {string.Join("", players.Select(p => $"P{p.Index}".PadLeft(4)))}  bot/late  nemesis");
            foreach (var p in players)
            {
                var cells = players.Select(q =>
                {
                    var n = kills.Count(k => k.Player == p.Index && k.Victim == q.Index);
                    return (q.Team == p.Team && n == 0 ? "·" : n.ToString()).PadLeft(4);
                });
                var unknown = kills.Count(k => k.Player == p.Index && k.Victim is null);
                var nemesis = kills.Where(k => k.Victim == p.Index).GroupBy(k => k.Player).OrderByDescending(g => g.Count()).FirstOrDefault();
                o.WriteLine($"  {L(p)}  {string.Join("", cells)}  {unknown,8}  {(nemesis is null ? "–" : $"P{nemesis.Key} ({nemesis.Count()})")}");
            }
        }

        // ---- Per player ----
        o.WriteLine($"\nPlayers (streak = kills in one life, film / scoreboard max spree; traded = died ≤{TradeWindowMs / 1000} s after a kill; revenge = killed their last killer)");
        o.WriteLine($"  {"".PadRight(w)}  kills deaths no-killer  streak  first kill  first death  traded revenge");
        var lifeLines = new List<string>();
        foreach (var p in players)
        {
            var mine = events.Select((e, i) => (e, i)).Where(x => x.e.Player == p.Index).ToList();
            var myKills = mine.Where(x => x.e.Kind == SummaryEventKind.PlayerKill).Select(x => x.e).ToList();
            var myDeaths = mine.Where(x => x.e.Kind == SummaryEventKind.Death).ToList();
            var noKiller = myDeaths.Count(x => !pairedDeath.Contains(x.i));

            int streak = 0, best = 0, revenge = 0; int? lastKiller = null;
            foreach (var (e, i) in mine)
            {
                if (e.Kind == SummaryEventKind.PlayerKill)
                {
                    best = Math.Max(best, ++streak);
                    if (lastKiller is not null && e.Victim == lastKiller) { revenge++; lastKiller = null; }
                }
                else if (e.Kind == SummaryEventKind.Death)
                {
                    streak = 0;
                    lastKiller = killerOf.TryGetValue(i, out var k) ? k : null;
                }
            }
            var traded = myKills.Count(k => myDeaths.Any(d => d.e.TimeMs >= k.TimeMs && d.e.TimeMs - k.TimeMs <= TradeWindowMs));
            var core = p.Row.Stats?.CoreStats;
            o.WriteLine($"  {L(p)}  {myKills.Count,5} {myDeaths.Count,6} {noKiller,9}  {$"{best}/{core?.MaxKillingSpree}",6}  " +
                        $"{(myKills.Count > 0 ? Fmt(myKills[0].TimeMs) : "–"),10}  {(myDeaths.Count > 0 ? Fmt(myDeaths[0].e.TimeMs) : "–"),11}  {traded,6} {revenge,7}");

            var gaps = myDeaths.Zip(myDeaths.Skip(1), (x, y) => (long)(y.e.TimeMs - x.e.TimeMs)).Order().ToList();
            var left = p.Row.ParticipationInfo?.LeftInProgress == true ? "  left early" : "";
            lifeLines.Add(gaps.Count == 0
                ? $"  {L(p)}  {myDeaths.Count + 1,5} {core?.Spawns,8}  {"–",22}  {"–",22}  {(core?.AverageLifeDuration is { } a0 ? Fmt(Ms(a0)) : "–"),10}{left}"
                : $"  {L(p)}  {myDeaths.Count + 1,5} {core?.Spawns,8}  {Fmt(Median(gaps)),10} / {Fmt(gaps[^1]),9}  " +
                  $"{Fmt(Math.Max(0, Median(gaps) - RespawnEstimateMs)),10} / {Fmt(Math.Max(0, gaps[^1] - RespawnEstimateMs)),9}  " +
                  $"{(core?.AverageLifeDuration is { } a ? Fmt(Ms(a)) : "–"),10}{left}");
        }
        o.WriteLine($"\nLives (time alive is ESTIMATED: death-to-death minus a {RespawnEstimateMs / 1000} s respawn)");
        o.WriteLine($"  {"".PadRight(w)}  lives  spawns   death-to-death med/max    est. time alive med/max  avg life (scoreboard)");
        foreach (var line in lifeLines) o.WriteLine(line);

        // ---- Calibration ----
        o.WriteLine("\nCalibration (estimates)");
        if (events.Count > 0)
            o.WriteLine($"  event clock: first event {Fmt(events.Min(e => e.TimeMs))}, last {Fmt(events.Max(e => e.TimeMs))}; " +
                        $"the match lasts {Fmt(durationMs)} (last event {(durationMs - events.Max(e => e.TimeMs)) / 1000.0:F1} s before its end); " +
                        $"the film is {(m.CustomData.FilmLength - durationMs) / 1000.0:F1} s longer than the match");
        // Not-alive time per full-match player = timePlayed − averageLifeDuration × spawns; fit against deaths.
        var fit = players
            .Where(p => p.Row.ParticipationInfo is { JoinedInProgress: false, LeftInProgress: false, TimePlayed: not null } && p.Row.Stats?.CoreStats.AverageLifeDuration is not null)
            .Select(p => (X: (double)p.Row.Stats!.CoreStats.Deaths,
                          Y: (Ms(p.Row.ParticipationInfo!.TimePlayed!) - Ms(p.Row.Stats.CoreStats.AverageLifeDuration!) * p.Row.Stats.CoreStats.Spawns) / 1000.0))
            .ToList();
        if (fit.Count >= 3 && fit.Select(f => f.X).Distinct().Count() > 1)
        {
            double mx = fit.Average(f => f.X), my = fit.Average(f => f.Y);
            var slope = fit.Sum(f => (f.X - mx) * (f.Y - my)) / fit.Sum(f => (f.X - mx) * (f.X - mx));
            o.WriteLine($"  this film's fit over {fit.Count} full-match players: time not alive = {slope:F1} s × deaths + {my - slope * mx:F0} s " +
                        $"(scoreboard average life is rounded to 0.1 s)");
        }
        o.WriteLine($"  used here: respawn {RespawnEstimateMs / 1000} s, first spawn about {FirstSpawnEstimateMs / 1000} s on the event clock (fits across five CTF films)");
        return 0;
    }

    private static long Ms(string iso) => (long)IsoDuration.Parse(iso).TotalMilliseconds;

    private static long Median(List<long> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    private static string Fmt(long ms) => TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss\.f" : @"mm\:ss\.f");
}
