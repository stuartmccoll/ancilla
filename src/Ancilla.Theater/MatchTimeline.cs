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

/// <summary>
/// A point on the map grid: 0 ≤ X &lt; <see cref="MatchTimeline.MapWidth"/>, 0 ≤ Y &lt;
/// <see cref="MatchTimeline.MapHeight"/>, Y down. The film's +Y is drawn up, so the map is a true
/// top-down view (not mirrored); which way is north isn't known.
/// </summary>
public readonly record struct MapPoint(double X, double Y);

/// <summary>
/// Where a player died and, when the killer's life is known, where the killer was. Facings are the
/// aim's yaw in degrees, anticlockwise from the map's +X (across), or null when no aim was recorded
/// in the second before.
/// </summary>
public sealed record DeathPoint(uint TimeMs, int Player, int Team, int? Killer, MapPoint At, double? Facing,
    MapPoint? KillerAt, double? KillerFacing)
{
    /// <summary>
    /// Degrees between where the victim was facing and the direction to the killer: 0 is looking
    /// straight at them, 180 is facing away. Null when either is unknown.
    /// </summary>
    public double? AngleOff => Facing is { } f && KillerAt is { } k && (k.X != At.X || k.Y != At.Y)
        ? MatchTimeline.AngleBetween(f, Math.Atan2(At.Y - k.Y, k.X - At.X) * 180 / Math.PI) : null;
}

/// <summary>
/// One navmesh polygon on the map grid. <see cref="Level"/> is its height in the area shown, 1 (lowest)
/// to <see cref="MatchTimeline.FloorLevels"/>; all 1 when the floor is flat.
/// </summary>
public sealed record FloorFace(IReadOnlyList<MapPoint> Points, int Level);

/// <summary>Where a player was at an objective event (in CTF, a flag action).</summary>
public sealed record ObjectivePoint(uint TimeMs, int Player, int Team, MapPoint At);

/// <summary>
/// One player's movement for the animated map, sampled every <see cref="Playback.StepMs"/>: positions
/// in tenths of a map square (X10, Y10; -1 where the player isn't placed: dead, not yet spawned, or a
/// life the film couldn't link to them) and facing in whole degrees, anticlockwise from +X (-1 when
/// unknown).
/// </summary>
public sealed record PlaybackTrack(int Player, int[] X10, int[] Y10, int[] Yaw);

/// <summary>Every linked player's movement through the match, for the animated map.</summary>
public sealed record Playback(int StepMs, int Frames, IReadOnlyList<PlaybackTrack> Tracks);

/// <summary>
/// A match's timeline from its Theater film: the kill feed, per-player figures, team kills over
/// time, head-to-head kills and, for PvP, a map: where players died and killed from, where objective
/// events happened, and how long each player spent in each cell (<see cref="TimeByCell"/>, ms,
/// indexed <c>y * MapWidth + x</c>, for the lives linked to them). With a Forge map's navmesh, the map
/// also has a <see cref="Floor"/>, lowest faces first, when the fit is good enough
/// (<see cref="FloorPlan.MinOnMesh"/>; <see cref="FloorFit"/> is kept either way). Built from a cached film (see
/// <see cref="FilmCache"/>) and the match's scoreboard. The film's scale is unknown, so the map is
/// drawn on a grid scaled to the area players covered.
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
    IReadOnlyList<ObjectivePoint> Objectives,
    IReadOnlyDictionary<int, float[]> TimeByCell,
    IReadOnlySet<(int X, int Y)> Arena,
    IReadOnlyList<FloorFace> Floor,
    FloorFit? FloorFit,
    Playback? Playback,
    int BotsAndUnlinked)
{
    public const int TradeWindowMs = 3_000;
    /// <summary>Longest gap between two records of a life that counts as time in one cell.</summary>
    public const double MaxStepMs = 1_000;
    /// <summary>How old an aim record can be and still count as the facing at a moment.</summary>
    public const double AimMaxAgeMs = 1_000;
    public const int FloorLevels = 4;
    /// <summary>Floors less than this many world units apart in height count as flat.</summary>
    private const double FlatFloor = 1.0;
    /// <summary>How many grid squares from where players went a floor face can be and still be drawn.</summary>
    private const int FloorReach = 2;
    public const int MapWidth = 120, MapHeight = 80;
    private const int FirefightCategory = 42;

    public bool HasPositions => Arena.Count > 0;

    public static MatchTimeline Build(FilmCache cache, string matchId, MatchStats board, bool positions = true, NavMesh? navMesh = null)
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
        var objectivePoints = new List<ObjectivePoint>();
        var timeByCell = new Dictionary<int, float[]>();
        var arena = new HashSet<(int, int)>();
        var floor = new List<FloorFace>();
        Playback? playback = null;
        FloorFit? fit = null;
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
                var aims = pos.Aims.GroupBy(a => a.Slot).ToDictionary(g => g.Key, g => g.Select(a => (a.Tick, a.Aim)).ToList());

                var all = pos.Bipeds.Select(b => b.Record.Position).ToList();
                int minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
                // One scale for both axes, so the map keeps its shape; centred in the grid. +Y is up.
                var scale = Math.Min((MapWidth - 1.0) / Math.Max(1, maxX - minX), (MapHeight - 1.0) / Math.Max(1, maxY - minY));
                var (padX, padY) = ((MapWidth - 1 - (maxX - minX) * scale) / 2, (MapHeight - 1 - (maxY - minY) * scale) / 2);
                MapPoint At2(double x, double y) => new(padX + (x - minX) * scale + .5, padY + (maxY - y) * scale + .5);
                MapPoint Cell(FilmPosition p) => At2(p.X, p.Y);
                foreach (var p in all) { var c = Cell(p); arena.Add(((int)c.X, (int)c.Y)); }
                if (navMesh is not null && (fit = FloorPlan.Fit(navMesh, all)) is { OnMesh: >= FloorPlan.MinOnMesh } good)
                {
                    // Faces players came near: the mesh can reach well beyond the playable space.
                    var near = arena.SelectMany(c => Enumerable.Range(-FloorReach, 2 * FloorReach + 1)
                        .SelectMany(dx => Enumerable.Range(-FloorReach, 2 * FloorReach + 1).Select(dy => (c.Item1 + dx, c.Item2 + dy)))).ToHashSet();
                    floor = PlaceFloor(navMesh, good, At2, near);
                }

                (int Slot, FilmPosition Position)? At(int player, int tick)
                {
                    foreach (var slot in lives.GetValueOrDefault(player) ?? [])
                    {
                        var t = tracks[slot];
                        if (tick < t.FirstTick || tick > t.LastTick + 3) continue;
                        var i = t.Points.FindLastIndex(x => x.Tick <= tick);
                        return i >= 0 ? (slot, t.Points[i].Position) : null;
                    }
                    return null;
                }
                double? Facing(int slot, int tick) =>
                    aims.TryGetValue(slot, out var list) && AimAt(list, tick) is { } a && pos.TickMs[tick] - pos.TickMs[a.Tick] <= AimMaxAgeMs
                        ? a.Aim.YawDegrees : null;

                foreach (var (n, slot) in matched)
                {
                    var at = tracks[slot].Points[^1].Position;
                    var killer = killerOf.TryGetValue(deathIdx[n], out var k) ? k : (int?)null;
                    var tick = deathTicks[n];
                    MapPoint? killerCell = null; double? killerFacing = null;
                    if (killer is { } kp && At(kp, tick) is { } ka)
                    {
                        (killDistances.TryGetValue(kp, out var l) ? l : killDistances[kp] = []).Add(at.HorizontalDistance(ka.Position));
                        (killerCell, killerFacing) = (Cell(ka.Position), Facing(ka.Slot, tick));
                    }
                    deathPoints.Add(new DeathPoint(deaths[n].TimeMs, deaths[n].Player, players[deaths[n].Player].Team, killer,
                        Cell(at), Facing(slot, Math.Min(tick, tracks[slot].LastTick)), killerCell, killerFacing));
                }
                deathPoints.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

                foreach (var e in events.Where(e => e.Kind == SummaryEventKind.Objective))
                    if (At(e.Player, pos.TickAt(e.TimeMs)) is { } oa)
                        objectivePoints.Add(new ObjectivePoint(e.TimeMs, e.Player, players[e.Player].Team, Cell(oa.Position)));

                foreach (var (player, slots) in lives)
                    timeByCell[player] = TimeSpent(slots.Select(s => tracks[s]), pos.TickMs, Cell);
                playback = BuildPlayback(lives, tracks, aims, pos, duration, Cell);
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

        return new MatchTimeline(players, teams, duration, feed, summaries, perMinute, h2h, deathPoints, objectivePoints, timeByCell, arena, floor, fit, playback, outsiders);
    }

    /// <summary>
    /// The navmesh faces placed on the map grid by <paramref name="fit"/> and <paramref name="toMap"/>
    /// (film X, Y to the grid) that cover or touch a square in <paramref name="near"/>, with height
    /// levels over the faces kept, lowest first so upper floors are drawn over lower ones.
    /// </summary>
    public static List<FloorFace> PlaceFloor(NavMesh mesh, FloorFit fit, Func<double, double, MapPoint> toMap, IReadOnlySet<(int X, int Y)> near)
    {
        var shown = new List<(MapPoint[] Points, float Height)>();
        foreach (var face in mesh.Faces)
        {
            var points = face.Points.Select(p => fit.ToFilm(p.X, p.Y)).Select(f => toMap(f.X, f.Y)).ToArray();
            if (Touches(points, near)) shown.Add((points, face.Height));
        }
        if (shown.Count == 0) return [];
        float low = shown.Min(f => f.Height), high = shown.Max(f => f.Height);
        int Level(float h) => high - low < FlatFloor ? 1 : 1 + Math.Min(FloorLevels - 1, (int)(FloorLevels * (h - low) / (high - low)));
        return shown.OrderBy(f => f.Height).Select(f => new FloorFace(f.Points, Level(f.Height))).ToList();
    }

    /// <summary>The step between animation samples.</summary>
    public const int PlaybackStepMs = 250;

    /// <summary>
    /// Samples each player's linked lives every <see cref="PlaybackStepMs"/>: the life's last position at or before
    /// the moment (none once its track has ended), and the aim from the second before.
    /// </summary>
    public static Playback BuildPlayback(Dictionary<int, List<int>> lives, Dictionary<int, BipedTrack> tracks,
        Dictionary<int, List<(int Tick, Aim Aim)>> aims, FilmPositions pos, uint durationMs, Func<FilmPosition, MapPoint> cell)
    {
        var frames = (int)(durationMs / PlaybackStepMs) + 1;
        var result = new List<PlaybackTrack>();
        foreach (var (player, slots) in lives.OrderBy(x => x.Key))
        {
            int[] x10 = new int[frames], y10 = new int[frames], yaw = new int[frames];
            Array.Fill(x10, -1); Array.Fill(y10, -1); Array.Fill(yaw, -1);
            foreach (var slot in slots)
            {
                var t = tracks[slot];
                var ticks = t.Points.Select(p => p.Tick).ToList();
                var slotAims = aims.GetValueOrDefault(slot);
                int first = (int)Math.Ceiling(pos.TickMs[t.FirstTick] / PlaybackStepMs), last = (int)(pos.TickMs[t.LastTick] / PlaybackStepMs);
                for (var f = Math.Max(0, first); f <= Math.Min(frames - 1, last); f++)
                {
                    var tick = pos.TickAt(f * (double)PlaybackStepMs);
                    var i = ticks.BinarySearch(tick); if (i < 0) i = ~i - 1;
                    if (i < 0) continue;
                    var c = cell(t.Points[i].Position);
                    (x10[f], y10[f]) = ((int)Math.Round(c.X * 10), (int)Math.Round(c.Y * 10));
                    if (slotAims is not null && AimAt(slotAims, tick) is { } a && pos.TickMs[tick] - pos.TickMs[a.Tick] <= AimMaxAgeMs)
                        yaw[f] = (int)Math.Round(a.Aim.YawDegrees) % 360;
                }
            }
            result.Add(new PlaybackTrack(player, x10, y10, yaw));
        }
        return new Playback(PlaybackStepMs, frames, result);
    }

    // Whether a polygon has a corner in one of the squares, or covers one's centre.
    private static bool Touches(MapPoint[] pts, IReadOnlySet<(int X, int Y)> squares)
    {
        if (pts.Any(p => squares.Contains(((int)Math.Floor(p.X), (int)Math.Floor(p.Y))))) return true;
        int x0 = Math.Max(0, (int)pts.Min(p => p.X)), x1 = Math.Min(MapWidth - 1, (int)pts.Max(p => p.X));
        int y0 = Math.Max(0, (int)pts.Min(p => p.Y)), y1 = Math.Min(MapHeight - 1, (int)pts.Max(p => p.Y));
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                if (squares.Contains((x, y)) && Inside(pts, x + .5, y + .5)) return true;
        return false;
    }

    private static bool Inside(MapPoint[] pts, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
            if ((pts[i].Y > y) != (pts[j].Y > y) && x < (pts[j].X - pts[i].X) * (y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>
    /// Milliseconds spent in each map cell (index <c>y * MapWidth + x</c>) over the given lives. Each
    /// record's position holds until the life's next record; gaps over <see cref="MaxStepMs"/> count
    /// only that long, so a missing stretch of records doesn't pile time on one cell.
    /// </summary>
    public static float[] TimeSpent(IEnumerable<BipedTrack> lives, IReadOnlyList<double> tickMs, Func<FilmPosition, MapPoint> cell)
    {
        var grid = new float[MapWidth * MapHeight];
        foreach (var life in lives)
            for (var i = 0; i + 1 < life.Points.Count; i++)
            {
                var ms = Math.Min(MaxStepMs, tickMs[life.Points[i + 1].Tick] - tickMs[life.Points[i].Tick]);
                var c = cell(life.Points[i].Position);
                int x = Math.Clamp((int)c.X, 0, MapWidth - 1), y = Math.Clamp((int)c.Y, 0, MapHeight - 1);
                grid[y * MapWidth + x] += (float)ms;
            }
        return grid;
    }

    /// <summary>
    /// Heatmap levels for a <see cref="TimeSpent"/> grid: each cell's time is averaged with its
    /// neighbours within <paramref name="radius"/> cells (a box blur, so paths read as areas), then the
    /// cells with any time are split into <paramref name="levels"/> equal-sized groups by that time,
    /// 1 (least) to <paramref name="levels"/> (most). Cells with no time are 0.
    /// </summary>
    public static byte[] HeatLevels(float[] grid, int levels = 6, int radius = 1)
    {
        var blurred = new float[grid.Length];
        for (var y = 0; y < MapHeight; y++)
            for (var x = 0; x < MapWidth; x++)
            {
                float sum = 0;
                for (var dy = -radius; dy <= radius; dy++)
                    for (var dx = -radius; dx <= radius; dx++)
                        if (x + dx is >= 0 and < MapWidth && y + dy is >= 0 and < MapHeight) sum += grid[(y + dy) * MapWidth + x + dx];
                blurred[y * MapWidth + x] = sum;
            }
        var order = Enumerable.Range(0, blurred.Length).Where(i => blurred[i] > 0).OrderBy(i => blurred[i]).ToList();
        var result = new byte[grid.Length];
        for (var r = 0; r < order.Count; r++) result[order[r]] = (byte)(1 + r * levels / order.Count);
        return result;
    }

    /// <summary>The last aim at or before a tick, from one slot's aims in tick order.</summary>
    public static (int Tick, Aim Aim)? AimAt(IReadOnlyList<(int Tick, Aim Aim)> aims, int tick)
    {
        int lo = 0, hi = aims.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (aims[mid].Tick <= tick) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found >= 0 ? aims[found] : null;
    }

    /// <summary>The angle between two directions in degrees, 0–180.</summary>
    public static double AngleBetween(double a, double b)
    {
        var d = Math.Abs((a - b) % 360);
        return d > 180 ? 360 - d : d;
    }
}
