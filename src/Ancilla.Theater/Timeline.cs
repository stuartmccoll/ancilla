using Ancilla.Core;
using Ancilla.Theater.Format;

namespace Ancilla.Theater;

/// <summary>
/// <c>Ancilla.Theater timeline</c>: <see cref="MatchTimeline"/> (what the web app's timeline page shows)
/// written as text, with checks against the scoreboard. Players are labelled "T{team} P{index}"
/// unless gamertags are asked for.
/// </summary>
public static class Timeline
{
    // Estimates, from five CTF films (see README): for 35 full-match players, the time the
    // scoreboard doesn't count as alive (timePlayed − averageLifeDuration × spawns) fits
    // 8.0 s per death + 38 s, and no player has an event before 35 s on the event clock.
    public const int RespawnEstimateMs = 8_000;
    public const int FirstSpawnEstimateMs = 35_000;

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

    /// <summary>
    /// Writes <see cref="MatchTimeline"/> as text, so the analysis (streaks, traded and revenge kills,
    /// nemesis, lives and kill distances) lives in one place, plus checks against the scoreboard and
    /// calibration notes. Returns 1 when no players can be decoded.
    /// </summary>
    public static int Run(FilmCache cache, string matchId, bool showNames, bool feed, TextWriter o)
    {
        var m = cache.LoadManifest(matchId);
        var board = cache.LoadScoreboard(matchId) ?? throw new InvalidDataException("No scoreboard cached for this film.");
        MatchTimeline t;
        try { t = MatchTimeline.Build(cache, matchId, board); }
        catch (InvalidDataException ex) when (ex.Message == MatchTimeline.NoPlayersMessage)
        {
            o.WriteLine("No players could be decoded from this film; run 'Ancilla.Theater inspect' to see why.");
            return 1;
        }
        if (t.RosterNotOnScoreboard > 0)
            throw new InvalidDataException("A roster player isn't on the scoreboard. Run 'Ancilla.Theater inspect'.");

        var rows = t.Players.Select(p => board.Players.First(r => r.Xuid == p.Xuid)).ToList();
        var players = t.Players.Select(p => new Player(p.Index, p.Team,
            $"T{p.Team} P{p.Index}" + (p.LateJoiner ? "*" : "") + (showNames ? $" {p.Gamertag}" : ""), rows[p.Index])).ToList();
        var outsiders = board.Players.Except(rows).ToList();

        var medalNames = cache.LoadMedals()?.Medals.ToDictionary(x => x.NameId, x => x.Name ?? x.NameId.ToString()) ?? [];
        string MedalName(int code) =>
            MedalCodes.Known.TryGetValue(code, out var id) && medalNames.TryGetValue(id, out var n) ? n : $"medal code {code}";

        var durationMs = board.MatchInfo.Duration is { } d ? Ms(d) : m.CustomData.FilmLength;
        var w = players.Max(p => p.Label.Length);
        string L(Player p) => p.Label.PadRight(w);
        var kills = t.Feed.Where(f => f.Kind == FeedKind.Kill).ToList();
        // Each decoded player's deaths: killed by another decoded player, or with no killer recorded.
        var deathTimes = players.Select(p => t.Feed
            .Where(f => (f.Kind == FeedKind.Kill && f.Other == p.Index) || (f.Kind == FeedKind.Death && f.Player == p.Index))
            .Select(f => (long)f.TimeMs).Order().ToList()).ToList();

        // ---- Overview ----
        var modes = board.Teams.FirstOrDefault()?.Stats.ModeStats.Keys.Where(k => k != "PvpStats").Select(k => k.Replace("Stats", "")) ?? [];
        o.WriteLine("Match timeline (times are on the summary's event clock; see Calibration)");
        o.WriteLine($"  mode category {board.MatchInfo.GameVariantCategory} ({string.Join(", ", modes)}), match {Fmt(durationMs)}, film {Fmt(m.CustomData.FilmLength)}");
        o.WriteLine($"  players: {players.Count} ({t.Players.Count(p => p.LateJoiner)} late joiner(s), marked *, linked by their kills, deaths and medals)");
        o.WriteLine($"  not decodable: {outsiders.Count(x => !x.IsHuman)} bot(s), {outsiders.Count(x => x.IsHuman)} other player(s) with no records or no link. " +
                    "Their kills and deaths show only as unmatched events.");
        o.WriteLine($"  events: {kills.Count} kills, {deathTimes.Sum(x => x.Count)} deaths, {t.Feed.Sum(f => f.MedalCodes.Count)} medals, " +
                    $"{t.Feed.Count(f => f.Kind == FeedKind.Objective && f.Flag is not null)} flag actions, " +
                    $"{t.Feed.Count(f => f.Kind == FeedKind.Objective && f.Flag is null)} other objective events (not shown), " +
                    $"{t.Pickups?.Count ?? 0} weapon pickups ({t.Pickups?.Count(p => p.Swapped) ?? 0} swapping out a weapon)");

        // ---- Kill feed ----
        if (feed)
        {
            o.WriteLine("\nKill feed");
            foreach (var f in t.Feed)
            {
                var who = L(players[f.Player]);
                var medals = f.MedalCodes.Select(MedalName).ToList();
                switch (f.Kind)
                {
                    case FeedKind.Kill:
                        var victim = f.Other is { } v ? players[v].Label : "bot or late joiner";
                        o.WriteLine($"  {Fmt(f.TimeMs)}  {who} → {victim}{(medals.Count > 0 ? "   [" + string.Join(", ", medals) + "]" : "")}");
                        break;
                    case FeedKind.Death:
                        o.WriteLine($"  {Fmt(f.TimeMs)}  {who} died: suicide, or killed by a bot or late joiner");
                        break;
                    case FeedKind.Medal:
                        o.WriteLine($"  {Fmt(f.TimeMs)}  {who} medal: {medals[0]}  (not at a kill)");
                        break;
                    case FeedKind.Objective when f.Flag is { } flag:
                        o.WriteLine($"  {Fmt(f.TimeMs)}  {who} flag: {flag}");
                        break;
                    case FeedKind.Pickup:
                        var pk = t.Pickups?.FirstOrDefault(x => x.TimeMs == f.TimeMs && x.Player == f.Player);
                        var weapon = pk?.Weapon is { } wid ? $"weapon {wid:x8}" : "a weapon";
                        o.WriteLine($"  {Fmt(f.TimeMs)}  {who} picked up {weapon} (weapon slot {pk?.WeaponSlot + 1}{(pk?.Swapped == true ? ", dropping the one it replaced" : "")})");
                        break;
                }
            }
        }

        // ---- Score flow ----
        o.WriteLine("\nScore flow (team kills)");
        var teams = t.Teams.ToList();
        if (teams.Count == 2 && kills.Count > 0)
        {
            int a = teams[0], b = teams[1];
            int ka = 0, kb = 0; int? leader = null;
            var changes = new List<string>();
            (int Lead, uint T) bestA = (0, 0), bestB = (0, 0);
            var nextMinute = 60_000L;
            o.WriteLine($"  time    T{a}  T{b}  lead");
            void Row(long time) => o.WriteLine($"  {Fmt(time)[..5]}  {ka,3} {kb,3}  {(ka == kb ? "level" : ka > kb ? $"T{a} +{ka - kb}" : $"T{b} +{kb - ka}")}");
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
            foreach (var team in teams)
            {
                var film = team == a ? ka : kb;
                var extra = outsiders.Where(x => x.LastTeamId == team).Sum(x => x.Stats?.CoreStats.Kills ?? 0);
                var sb = board.Teams.FirstOrDefault(x => x.TeamId == team)?.Stats.CoreStats;
                var check = sb is null ? "" : film + extra == sb.Kills ? "  [matches]" : "  [DIFFERS]";
                o.WriteLine($"  T{team}: {film} kills in the film + {extra} by bots and late joiners = {film + extra}; scoreboard team kills {sb?.Kills}{check}");
            }
            o.WriteLine($"  team score (scoreboard): {string.Join(", ", board.Teams.Select(x => $"T{x.TeamId} {x.Stats.CoreStats.Score}"))}" +
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
                var cells = players.Select(q => (q.Team == p.Team && t.HeadToHead[p.Index, q.Index] == 0 ? "·" : t.HeadToHead[p.Index, q.Index].ToString()).PadLeft(4));
                var unknown = kills.Count(k => k.Player == p.Index && k.Other is null);
                var nemesis = t.Summaries[p.Index].Nemesis is { } n ? $"P{n} ({t.HeadToHead[n, p.Index]})" : "–";
                o.WriteLine($"  {L(p)}  {string.Join("", cells)}  {unknown,8}  {nemesis}");
            }
        }

        // ---- Per player ----
        o.WriteLine($"\nPlayers (streak = kills in one life, film / scoreboard max spree; traded = died ≤{MatchTimeline.TradeWindowMs / 1000} s after a kill; revenge = killed their last killer)");
        o.WriteLine($"  {"".PadRight(w)}  kills deaths no-killer  streak  first kill  first death  traded revenge");
        var lifeLines = new List<string>();
        foreach (var p in players)
        {
            var s = t.Summaries[p.Index];
            var noKiller = t.Feed.Count(f => f.Kind == FeedKind.Death && f.Player == p.Index);
            var core = p.Row.Stats?.CoreStats;
            o.WriteLine($"  {L(p)}  {s.Kills,5} {s.Deaths,6} {noKiller,9}  {$"{s.LongestStreak}/{core?.MaxKillingSpree}",6}  " +
                        $"{(s.FirstKillMs is { } fk ? Fmt(fk) : "–"),10}  {(s.FirstDeathMs is { } fd ? Fmt(fd) : "–"),11}  {s.Traded,6} {s.Revenge,7}");

            var mine = deathTimes[p.Index];
            var gaps = mine.Zip(mine.Skip(1), (x, y) => y - x).Order().ToList();
            var left = p.Row.ParticipationInfo?.LeftInProgress == true ? "  left early" : "";
            lifeLines.Add(gaps.Count == 0
                ? $"  {L(p)}  {mine.Count + 1,5} {core?.Spawns,8}  {"–",22}  {"–",22}  {(core?.AverageLifeDuration is { } a0 ? Fmt(Ms(a0)) : "–"),10}{left}"
                : $"  {L(p)}  {mine.Count + 1,5} {core?.Spawns,8}  {Fmt(Median(gaps)),10} / {Fmt(gaps[^1]),9}  " +
                  $"{Fmt(Math.Max(0, Median(gaps) - RespawnEstimateMs)),10} / {Fmt(Math.Max(0, gaps[^1] - RespawnEstimateMs)),9}  " +
                  $"{(core?.AverageLifeDuration is { } a ? Fmt(Ms(a)) : "–"),10}{left}");
        }
        o.WriteLine($"\nLives (time alive is ESTIMATED: death-to-death minus a {RespawnEstimateMs / 1000} s respawn)");
        o.WriteLine($"  {"".PadRight(w)}  lives  spawns   death-to-death med/max    est. time alive med/max  avg life (scoreboard)");
        foreach (var line in lifeLines) o.WriteLine(line);

        // ---- Positions ----
        if (t.HasPositions) WritePositions(o, t, players, L, deathTimes.Sum(x => x.Count));
        else if (board.MatchInfo.GameVariantCategory != FirefightCategory)
            o.WriteLine("\nPositions\n  too few biped positions or deaths to place players");

        // ---- Calibration ----
        o.WriteLine("\nCalibration (estimates)");
        var times = t.Feed.Select(f => (long)f.TimeMs).ToList();
        if (times.Count > 0)
            o.WriteLine($"  event clock: first event {Fmt(times.Min())}, last {Fmt(times.Max())}; " +
                        $"the match lasts {Fmt(durationMs)} (last event {(durationMs - times.Max()) / 1000.0:F1} s before its end); " +
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

    private const int FirefightCategory = 42;
    public const int MapWidth = 60, MapHeight = 22;

    /// <summary>
    /// Per-player lives placed, movement and kill distances, and a text map of where each team died,
    /// from <see cref="MatchTimeline"/>. Distances are in film units (X and Y share one scale, about
    /// 72 to a world unit on most maps, finer on some; see FORMAT.md).
    /// </summary>
    private static void WritePositions(TextWriter o, MatchTimeline t, List<Player> players, Func<Player, string> L, int deaths)
    {
        o.WriteLine("\nPositions (film units: about 72 to a world unit on most maps, finer on some; Z is height, not used here)");
        o.WriteLine($"  {"".PadRight(L(players[0]).Length)}  lives found  distance moved  kill distance med (kills placed)");
        foreach (var p in players)
        {
            var s = t.Summaries[p.Index];
            var placed = t.Deaths.Count(d => d.Killer == p.Index && d.KillerAt is not null);
            o.WriteLine($"  {L(p)}  {$"{s.LivesPlaced}/{p.Row.Stats?.CoreStats.Spawns}",11}  {s.DistanceMoved,14:N0}  " +
                        (s.KillDistance is not { } kd ? "–" : $"{kd,17:N0} ({placed})"));
        }
        o.WriteLine($"  lives found / scoreboard spawns: a life is placed when a death ends it or it follows one; {t.Deaths.Count} of {deaths} deaths placed");

        // Death map: the timeline's grid, shrunk to text. Cells players went are ·; each placed death marks its team.
        (int Col, int Row) Cell(double x, double y) =>
            (Math.Clamp((int)(x * MapWidth / MatchTimeline.MapWidth), 0, MapWidth - 1), Math.Clamp((int)(y * MapHeight / MatchTimeline.MapHeight), 0, MapHeight - 1));
        var visited = t.Arena.Select(c => Cell(c.X + .5, c.Y + .5)).ToHashSet();
        var teams = t.Teams.Take(2).ToList();
        var deathsAt = new Dictionary<(int, int), int[]>();
        foreach (var d in t.Deaths)
        {
            var team = teams.IndexOf(d.Team);
            if (team < 0) continue;
            var c = Cell(d.At.X, d.At.Y);
            (deathsAt.TryGetValue(c, out var a) ? a : deathsAt[c] = new int[2])[team]++;
        }
        o.WriteLine($"\nDeath map (X across, Y up; · = where players went; {(teams.Count > 0 ? $"a = T{teams[0]}" : "")}" +
                    $"{(teams.Count > 1 ? $", b = T{teams[1]}" : "")} died here, A/B = 3 or more, * = both teams)");
        for (var row = 0; row < MapHeight; row++)
        {
            var line = new char[MapWidth];
            for (var col = 0; col < MapWidth; col++)
                line[col] = deathsAt.TryGetValue((col, row), out var d)
                    ? d[0] > 0 && d[1] > 0 ? '*' : d[0] > 0 ? (d[0] >= 3 ? 'A' : 'a') : (d[1] >= 3 ? 'B' : 'b')
                    : visited.Contains((col, row)) ? '·' : ' ';
            o.WriteLine($"  |{new string(line)}|");
        }
    }

    private static long Ms(string iso) => (long)IsoDuration.Parse(iso).TotalMilliseconds;

    private static long Median(List<long> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    private static string Fmt(long ms) => TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss\.f" : @"mm\:ss\.f");
}
