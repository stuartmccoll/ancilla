using System.Buffers.Binary;
using Ancilla.Core;
using Ancilla.Theater;
using Ancilla.Theater.Format;

// Ancilla.Theater: decodes Halo Infinite Theater films. For now a console app for researching the
// format and prototyping match analysis; the web app doesn't reference it yet.
// The Spartan token comes only from HALO_SPARTAN_TOKEN and is never printed or written to disk.

const int GameVariantFirefight = 42;
var cache = new FilmCache(FilmCache.DefaultRoot);
var command = args.FirstOrDefault() ?? "help";
var showNames = args.Contains("--show-names");
var rest = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

try
{
    return command switch
    {
        "fetch" => await FetchManyAsync(rest.FirstOrDefault()),
        "list" => List(),
        "inspect" => Inspect(ResolveMatch(rest.FirstOrDefault())),
        "timeline" => Timeline.Run(cache, ResolveMatch(rest.FirstOrDefault()), showNames, !args.Contains("--no-feed"), Console.Out),
        "track" => Track(ResolveMatch(rest.FirstOrDefault())),
        "rounds" => RoundsFired(ResolveMatch(rest.FirstOrDefault())),
        "dump" => Dump(ResolveMatch(rest.FirstOrDefault()), rest.Length > 1 ? ParseNumber(rest[1], "chunk index") : 0),
        _ => Help(),
    };
}
catch (Exception ex)
{
    // Expected failures (network, bad input, unreadable films) print just the message; anything
    // else names its type too, to help track it down. Never a stack trace.
    var expected = ex is HttpRequestException or InvalidDataException or ArgumentException or IOException;
    Console.Error.WriteLine(expected ? $"error: {ex.Message}" : $"error ({ex.GetType().Name}): {ex.Message}");
    return 1;
}

int ParseNumber(string text, string what) =>
    int.TryParse(text, out var n) && n >= 0 ? n : throw new ArgumentException($"The {what} must be a whole number of 0 or more, not '{text}'.");

// The value of a --name=N option, or the fallback when it isn't given.
int NumberOption(string name, int fallback) =>
    args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal)) is { } arg
        ? ParseNumber(arg[(name.Length + 1)..], $"{name} value")
        : fallback;

int Help()
{
    Console.WriteLine("""
        Ancilla.Theater fetch [matchId]        download a film (default: your latest matchmade match); needs HALO_SPARTAN_TOKEN
                              --pvp          latest matchmade match that isn't Firefight
                              --count=N      the latest N (of your last 25), one per mode category first
                              --start=N      skip your N most recent matches first (to reach older modes)
                              --custom       look in your custom games instead of matchmade ones
        Ancilla.Theater list                   films in the cache
        Ancilla.Theater inspect [matchId]      decode what is understood so far and check it (default: newest cached)
                                --show-names   include gamertags in the roster output
                                --learn        propose medals for summary medal codes not in the table yet
        Ancilla.Theater timeline [matchId]     kill feed, score flow, head-to-head and per-player lives (default: newest cached)
                                --show-names   label players with gamertags instead of T0 P1
                                --no-feed      leave out the kill feed
        Ancilla.Theater track [matchId]        biped positions as CSV (tick, ms, slot, x, y, z), for controlled tests
        Ancilla.Theater dump [matchId] [index] write chunk <index> decompressed, for hex tools
        """);
    Console.WriteLine($"Cache: {cache.DisplayRoot}");
    return 0;
}

async Task<int> FetchManyAsync(string? matchId)
{
    var token = Environment.GetEnvironmentVariable("HALO_SPARTAN_TOKEN");
    if (string.IsNullOrWhiteSpace(token)) { Console.Error.WriteLine("Set HALO_SPARTAN_TOKEN first."); return 1; }

    // No automatic redirects: a redirected request would carry the token's header to another host.
    using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Ancilla-Theater/0.1");
    var client = new HaloWaypointClient(http, token);
    var films = new FilmSource(http);

    // Refreshed on every fetch, so medals added in later seasons are known.
    try { cache.SaveMedals(await client.GetMedalMetadataAsync()); }
    catch (HttpRequestException ex) { Console.Error.WriteLine($"Medal metadata not refreshed: {ex.Message}"); }

    if (matchId is not null) return await FetchAsync(client, films, matchId);

    var count = NumberOption("--count", 1);
    // --start pages further back through match history, to reach modes the latest 25 don't cover.
    var start = NumberOption("--start", 0);
    var me = await client.GetMeAsync();
    var custom = args.Contains("--custom");
    var history = await client.GetMatchHistoryAsync(me.Xuid, start, 25, custom ? MatchHistoryType.Custom : MatchHistoryType.Matchmaking);
    // --pvp skips Firefight (mode category 42).
    var candidates = history.Results
        .Where(r => !args.Contains("--pvp") || r.MatchInfo.GameVariantCategory != GameVariantFirefight)
        .Where(r => HaloIds.IsGuid(r.MatchId))
        .ToList();
    // Newest match of each mode category first, then the rest newest first, so a batch covers more modes.
    var firstOfEach = candidates.GroupBy(r => r.MatchInfo.GameVariantCategory).Select(g => g.First()).ToList();
    var picks = firstOfEach.Concat(candidates.Except(firstOfEach)).Take(count).ToList();
    if (picks.Count == 0) throw new InvalidDataException($"No matching {(custom ? "custom" : "matchmade")} match in matches {start + 1}–{start + 25} of your history.");

    var failed = 0;
    foreach (var r in picks)
    {
        Console.WriteLine($"Mode category {r.MatchInfo.GameVariantCategory}, {(r.MatchInfo.Duration ?? "?")}");
        try { await FetchAsync(client, films, r.MatchId); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            Console.Error.WriteLine($"\n  skipped: {(ex is TaskCanceledException ? "timed out" : ex.Message)}");
            failed++;
        }
    }
    return failed == 0 ? 0 : 1;
}

async Task<int> FetchAsync(HaloWaypointClient client, FilmSource films, string matchId)
{
    HaloIds.RequireGuid(matchId, nameof(matchId));

    var raw = await client.GetFilmManifestJsonAsync(matchId);
    var manifest = FilmSource.ParseManifest(raw);
    cache.SaveManifest(matchId, raw);
    cache.SaveScoreboard(matchId, await client.GetMatchStatsAsync(matchId));

    var chunks = manifest.CustomData.Chunks;
    Console.WriteLine($"Match {matchId}: film v{manifest.CustomData.FilmMajorVersion}, {chunks.Count} chunks, " +
                      $"{chunks.Sum(c => c.ChunkSize) / 1_048_576.0:F1} MB");
    var done = 0;
    await Parallel.ForEachAsync(chunks, new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (c, ct) =>
    {
        if (!cache.HasChunk(matchId, c)) cache.SaveChunk(matchId, c, await films.GetChunkAsync(manifest, c, ct));
        Console.Write($"\r  {Interlocked.Increment(ref done)}/{chunks.Count}");
    });
    Console.WriteLine($"\nSaved to {cache.DisplayRoot}");
    return 0;
}

int List()
{
    foreach (var id in cache.CachedMatches())
    {
        var m = cache.LoadManifest(id);
        Console.WriteLine($"{id}  v{m.CustomData.FilmMajorVersion}  {m.CustomData.Chunks.Count} chunks  {TimeSpan.FromMilliseconds(m.CustomData.FilmLength):mm\\:ss}");
    }
    return 0;
}

// Checks on the replication chunks' packets: the type-1 entity slot table, types 6, 8 and 12, the
// type-10 tick markers, and the mapping from packet time to the summary's event times.
void InspectReplication(Action<bool, string> check, List<SlotTable> tables, List<SummaryEvent> events, MatchStats? board, int humanCount,
    (bool Type6Ok, bool Type12Ok, uint Type8MaxCount) leading, (int Count, int LayoutMisses, int FollowedByType0, int MaxPresent) ticks,
    List<(double Ms, int Bytes)> tickData)
{
    Console.WriteLine("\nReplication stream (type-2 chunks)");

    // Type 1: the entity slot table.
    check(tables.Count > 0 && tables.All(t => t.LayoutOk),
        $"every type-1 packet is a {SlotTable.PayloadBytes:N0}-byte entity slot table, every set bit in a decoded field ({tables.Count} snapshots)");
    check(tables.All(t => Enumerable.Range(0, SlotTable.StaticSlots).All(s => t.InUse[s])),
        $"slots 0–{SlotTable.StaticSlots - 1} (static entities) are in use in every snapshot");
    check(tables.Count > 0 && tables[0].Cursors[0] == SlotTable.StaticSlots, $"the first pool's cursor starts at {SlotTable.StaticSlots}");

    bool generationsOk = true, cursorsOk = true, movesOk = true;
    var moves = 0;
    for (var i = 0; i < tables.Count; i++)
    {
        var t = tables[i];
        for (var k = 0; k < SlotTable.CursorPools.Length; k++)
        {
            var (first, end) = SlotTable.CursorPools[k];
            var cursor = (int)t.Cursors[k];
            cursorsOk &= cursor == 0 || (cursor > first && cursor <= end);
            // Until a pool wraps, its cursor is 1 + the highest slot used in it.
            if (t.HighWater(first, end) is var hw && hw < end) cursorsOk &= cursor == hw;
            // Whenever a cursor moves, the slot just behind it was allocated since the last snapshot.
            if (i > 0 && cursor != 0 && cursor != tables[i - 1].Cursors[k])
            {
                moves++;
                movesOk &= t.Generation[cursor - 1] > tables[i - 1].Generation[cursor - 1];
            }
        }
        if (i == 0) continue;
        var prev = tables[i - 1];
        for (var s = 0; s < SlotTable.SlotCount; s++)
        {
            generationsOk &= t.Generation[s] >= prev.Generation[s];
            if (!prev.InUse[s] && t.InUse[s]) generationsOk &= t.Generation[s] > prev.Generation[s];
        }
    }
    check(generationsOk, "slot generations never go down, and a slot that comes into use always gets a new one");
    check(cursorsOk, "each pool's cursor is 1 + its highest used slot until the pool wraps");
    check(movesOk, $"whenever a cursor moves, the slot behind it was just allocated ({moves} moves)");

    if (board is not null && board.MatchInfo.GameVariantCategory != GameVariantFirefight && tables.Count > 0)
    {
        // One new biped slot per spawn; spawns after the last snapshot aren't in it yet.
        var spawns = board.Players.Sum(p => p.Stats?.CoreStats.Spawns ?? 0);
        var allocations = tables[^1].Allocations(SlotTable.BipedPool.First, SlotTable.BipedPool.End);
        check(spawns - allocations is >= 0 and <= 3,
            $"biped slots allocated by the last snapshot ({allocations}) are at most 3 short of the scoreboard's spawns ({spawns})");
    }

    // Types 6, 8 and 12.
    check(leading.Type6Ok, "type-6 packets are always 4 zero bytes");
    check(leading.Type12Ok, "type-12 packets are 4 bytes exactly when their bot count is 0");
    if (board is not null)
        check(leading.Type8MaxCount <= humanCount, $"type-8 player counts (up to {leading.Type8MaxCount}) never exceed the scoreboard's humans ({humanCount})");

    // Type 10: one 6-bit value per present player slot. A handful of packets in 300,000 don't fit.
    check(ticks.Count > 0 && ticks.LayoutMisses * 10_000 <= ticks.Count,
        $"type-10 packets fit the 32-slot layout ({ticks.Count - ticks.LayoutMisses:N0} of {ticks.Count:N0})");
    check(ticks.FollowedByType0 == ticks.Count, $"every type-10 packet is followed by a type-0 packet ({ticks.FollowedByType0:N0} of {ticks.Count:N0})");
    if (board is not null)
        check(ticks.MaxPresent <= humanCount, $"type-10 present slots (up to {ticks.MaxPresent}) never exceed the scoreboard's humans ({humanCount})");

    // Packet time is match time: the type-0 packet is several times its usual size on the tick of a
    // death, so the largest one within 5 ticks of each death should sit within 10 ms of it.
    var deaths = events.Where(e => e.Kind == SummaryEventKind.Death).ToList();
    if (deaths.Count > 0 && tickData.Count > 0)
    {
        var times = tickData.Select(x => x.Ms).ToList();
        var offsets = new List<double>();
        foreach (var d in deaths)
        {
            var at = times.BinarySearch(d.TimeMs) is var b && b < 0 ? ~b : b;
            var best = Math.Max(0, at - 5);
            for (var j = best; j <= Math.Min(times.Count - 1, at + 5); j++) if (tickData[j].Bytes > tickData[best].Bytes) best = j;
            offsets.Add(d.TimeMs - times[best]);
        }
        var within = offsets.Count(o => o is >= -10 and < 10);
        var median = offsets.Order().ElementAt(offsets.Count / 2);
        // A few deaths share a busy moment with a bigger packet; a film with few deaths gets the median test only.
        check(Math.Abs(median) <= 5 && (deaths.Count < 20 || within * 10 >= deaths.Count * 9),
            $"event times are packet times from the film's first packet: {within} of {deaths.Count} deaths sit on a packet-size spike within 10 ms (median {median:F1} ms)");
    }
}

// Checks on biped records read past their position (EntityRecords.FindBipedUpdates): the command
// tick, aim and ammo. Only records whose command tick landed are used.
void InspectBipedUpdates(Action<bool, string> check, List<(int Tick, BipedUpdate Update)> updates)
{
    Console.WriteLine("\nBiped components (type-0 records past the position)");
    var withTick = updates.Where(u => u.Update.CommandTick is not null).ToList();
    Console.WriteLine($"  {updates.Count:N0} records whose command tick landed, in {updates.Select(u => u.Update.Slot).Distinct().Count()} slots");
    if (withTick.Count < 100) { Console.WriteLine("  too few to check"); return; }

    // (a) The command tick is the packet count plus a per-film offset, which steps up a few times
    // (probably ticks missing from the film).
    var offsets = withTick.CountBy(u => (u.Update.CommandTick!.Value - u.Tick) & 0xFF).OrderByDescending(x => x.Value).ToList();
    var top = offsets.Take(4).Sum(x => x.Value);
    // 98.8–99.9% per film; the rest are chance matches of a biped header.
    check(top * 50 >= withTick.Count * 49,
        $"command ticks are the packet count plus the film's offset (its four most common, {string.Join(", ", offsets.Take(4).Select(x => x.Key))}): {top:N0} of {withTick.Count:N0}");

    // (b) Pitch stays within 90° of level.
    var aims = updates.Where(u => u.Update.Aim is not null).ToList();
    var level = aims.Count(u => Math.Abs(u.Update.Aim!.Value.PitchDegrees) <= 90);
    check(level * 200 >= aims.Count * 199, $"aim pitch is within 90° of level: {level:N0} of {aims.Count:N0}");

    // (c) Aim turns smoothly: consecutive updates of one slot up to 3 ticks apart differ by less than 22.5° in yaw.
    int smooth = 0, pairs = 0;
    foreach (var g in aims.GroupBy(u => u.Update.Slot))
        foreach (var (a, b) in g.Zip(g.Skip(1)))
        {
            if (b.Tick - a.Tick is < 1 or > 3) continue;
            pairs++;
            var d = Math.Abs(a.Update.Aim!.Value.Yaw - b.Update.Aim!.Value.Yaw);
            if (Math.Min(d, Aim.UnitsPerTurn - d) < 256) smooth++;
        }
    // 99.3–99.9% per film.
    check(pairs > 0 && smooth * 100 >= pairs * 99, $"aim turns smoothly: yaw moves less than 22.5° between updates up to 3 ticks apart in {smooth:N0} of {pairs:N0}");

    // (d) Ammo counts go down one round at a time between reloads. Bigger drops are records with
    // no command tick to check (an ammo component on its own), which aren't used here.
    int down = 0, oneRound = 0, up = 0;
    foreach (var g in updates.Where(u => u.Update.Rounds is not null).GroupBy(u => (u.Update.Slot, u.Update.AmmoComponent)))
        foreach (var (a, b) in g.Zip(g.Skip(1)))
        {
            var d = b.Update.Rounds!.Value - a.Update.Rounds!.Value;
            if (d < 0) down++;
            if (d == -1) oneRound++;
            if (d > 0) up++;
        }
    // 96–98% in matchmade films; 86% in a short custom game where many records had the ammo component alone.
    check(down > 0 && oneRound * 5 >= down * 4,
        $"ammo counts drop one round at a time: {oneRound:N0} of {down:N0} drops ({up:N0} rises: reloads and pickups)");
}

// Checks that type-0 biped records carry player positions, against the summary's deaths. PvP only:
// in Firefight the AI share the biped slots and AI deaths aren't in the summary.
void InspectPositions(Action<bool, string> check, List<SummaryEvent> events, IReadOnlyList<int> teamOf, IReadOnlyList<int> spawnsOf, List<(double Ms, int Bytes)> tickData,
    List<(int Tick, EntityPosition Record)> bipeds, List<(int Tick, EntityPosition Record)> objects)
{
    Console.WriteLine("\nPositions (type-0 entity records)");
    var deaths = events.Where(e => e.Kind == SummaryEventKind.Death).ToList();
    var tracks = Lives.Tracks(bipeds);
    Console.WriteLine($"  {bipeds.Count:N0} biped position records in {tracks.Count} slots; {objects.Count:N0} dropped-object records");
    if (deaths.Count < 20 || tracks.Count < 8) { Console.WriteLine("  too few deaths or bipeds to check"); return; }

    var times = tickData.Select(x => x.Ms).ToList();
    int TickOf(double ms) => (times.BinarySearch(ms) is var b && b >= 0 ? b + 1 : ~b) - 1;   // last tick at or before
    var deathTick = deaths.Select(d => TickOf(d.TimeMs)).ToList();
    var deathMs = deaths.Select(d => (double)d.TimeMs).ToList();
    FilmPosition FirstAt(int slot) => tracks[slot].Points[0].Position;
    FilmPosition LastAt(int slot) => tracks[slot].Points[^1].Position;

    // (a) Each death ends one slot's track within 3 ticks; deaths shifted by 2 s give the baseline.
    var matched = Lives.MatchDeaths(deathTick, tracks); var shifted = Lives.MatchDeaths(deathTick, tracks, shift: 120);
    check(matched.Count * 10 >= deaths.Count * 9 && shifted.Count * 10 <= deaths.Count,
        $"deaths end a biped slot's track within 3 ticks: {matched.Count} of {deaths.Count} (deaths shifted 2 s: {shifted.Count})");

    // Respawn timing, for information: a new slot first seen in a window after each matched death.
    int Respawns(double lo, double hi) => Lives.Respawns(deathMs, matched.Keys, tracks, times, lo, hi).Count;
    Console.WriteLine($"  [--] a new slot starts 7.95–8.25 s after {Respawns(7950, 8250)} of {matched.Count} matched deaths " +
                      $"(7.95–9 s: {Respawns(7950, 9000)}; 3.95–4.25 s: {Respawns(3950, 4250)}). CTF respawn is 8 s; one film spreads to 9 s.");

    // Lives chain: the slot that respawns a player should be the one their next death ends.
    var respawns = Lives.Respawns(deathMs, matched.Keys, tracks, times);
    int agree = 0, pairs = 0;
    for (var n = 0; n < deaths.Count; n++)
    {
        if (!respawns.TryGetValue(n, out var next)) continue;
        var later = Enumerable.Range(n + 1, deaths.Count - n - 1).FirstOrDefault(m => deaths[m].Player == deaths[n].Player, -1);
        if (later < 0 || !matched.TryGetValue(later, out var ended)) continue;
        pairs++; if (ended == next) agree++;
    }
    // Information, not a check: 85–100% per film, lower when several players respawn together.
    Console.WriteLine($"  [--] the new slot nearest 8 s after a death is the one the player's next death ends: {agree} of {pairs}");
    var lives = Lives.Chain(deaths.Select(d => d.Player).ToList(), matched, respawns, tracks);
    var livesMatchSpawns = Enumerable.Range(0, spawnsOf.Count).Count(p => lives.GetValueOrDefault(p)?.Count == spawnsOf[p]);
    // Lives without a death at either end (a player with no deaths, a quitter's last life) can't be
    // chained, so a player can be short; never over.
    check(lives.All(l => l.Key < spawnsOf.Count && l.Value.Count <= spawnsOf[l.Key]),
        $"no player has more chained lives than scoreboard spawns; {livesMatchSpawns} of {spawnsOf.Count} have exactly as many " +
        $"({lives.Values.Sum(l => l.Count)} lives chained, {spawnsOf.Sum()} spawns)");

    // (b) Objects dropped 0–300 ms after a death land on the victim's last position.
    var objectFirst = objects.GroupBy(o => o.Record.Slot).Select(g => g.First()).ToList();
    var drops = new Dictionary<int, List<FilmPosition>>();
    foreach (var (tick, rec) in objectFirst)
    {
        var j = deathTick.FindLastIndex(dk => dk <= tick);
        if (j >= 0 && times[tick] - deaths[j].TimeMs is >= 0 and < 300) (drops.TryGetValue(j, out var l) ? l : drops[j] = []).Add(rec.Position);
    }
    var keys = matched.Keys.Where(drops.ContainsKey).ToList();
    if (keys.Count >= 10)
    {
        double Nearest(int death, int dropsOf) => drops[dropsOf].Min(p => LastAt(matched[death]).HorizontalDistance(p));
        var real = keys.Select(n => Nearest(n, n)).Order().ToList();
        var other = keys.Select((n, i) => Nearest(n, keys[(i + 1) % keys.Count])).Order().ToList();
        double real50 = real[real.Count / 2], other50 = other[other.Count / 2];
        check(real50 <= 0.2 * other50, $"objects dropped at a death land on the victim: median {real50:F0} units horizontally, against {other50:F0} for another death's ({keys.Count} deaths)");
    }

    // (c) Steps between consecutive ticks are small (Z counts half).
    var steps = tracks.Values.SelectMany(v => v.Points.Zip(v.Points.Skip(1)).Where(x => x.Second.Tick == x.First.Tick + 1)
        .Select(x => (x.First.Position, x.Second.Position) is var (a, b)
            ? Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow((a.Z - b.Z) / 2.0, 2)) : 0)).ToList();
    var small = steps.Count(s => s <= 6);
    check(steps.Count > 0 && small * 10 >= steps.Count * 9, $"biped positions move smoothly: {small:N0} of {steps.Count:N0} tick-to-tick steps are 6 units or less");

    // (e) A new life starts at the team's base: the first eight slots' first positions form two
    // clusters (the bases), and each team's matched slots should start at one of them.
    var firstEight = tracks.Keys.OrderBy(s => tracks[s].FirstTick).Take(8).ToList();
    var c0 = FirstAt(firstEight[0]);
    var c1 = FirstAt(firstEight.MaxBy(s => FirstAt(s).HorizontalDistance(c0)));
    int BaseOf(int s) => FirstAt(s).HorizontalDistance(c0) <= FirstAt(s).HorizontalDistance(c1) ? 0 : 1;
    var byTeam = matched.GroupBy(m => teamOf[deaths[m.Key].Player]).Select(g => g.CountBy(m => BaseOf(m.Value)).Max(x => x.Value)).Sum();
    check(byTeam * 10 >= matched.Count * 9, $"each team's lives start at one base: {byTeam} of {matched.Count} matched slots");
}

string ResolveMatch(string? matchId) =>
    matchId is not null
        ? HaloIds.RequireGuid(matchId, nameof(matchId))
        : cache.NewestMatch() ?? throw new InvalidDataException("No films cached. Run 'Ancilla.Theater fetch' first.");

// Every biped candidate in a slot seen at least 30 times, unfiltered by coordinate range: a solo
// custom game may drop no objects, which is where FilterBipeds takes its range from.
int Track(string matchId)
{
    var m = cache.LoadManifest(matchId);
    var ticks = new List<double>();
    var bipeds = new List<(int Tick, EntityPosition Record)>();
    ulong? firstUs = null;
    foreach (var c in m.CustomData.Chunks.Where(c => c.ChunkType == 2).OrderBy(c => c.Index))
        foreach (var p in PacketReader.Read(cache.LoadChunk(matchId, c), out _))
        {
            firstUs ??= p.TimestampUs;
            if (p.Type != 0) continue;
            bipeds.AddRange(EntityRecords.Find(p.Payload.Span).Bipeds.Select(r => (ticks.Count, r)));
            ticks.Add((p.TimestampUs - firstUs.Value) / 1000.0);
        }
    var perSlot = bipeds.CountBy(b => b.Record.Slot).ToDictionary();
    Console.WriteLine("tick,ms,slot,x,y,z");
    foreach (var (tick, (slot, pos)) in bipeds.Where(b => perSlot[b.Record.Slot] >= 30))
        Console.WriteLine($"{tick},{ticks[tick]:F1},{slot},{pos.X},{pos.Y},{pos.Z}");
    return 0;
}

// Research: rounds fired per player, counted from biped ammo records, against the scoreboard's
// shots fired. Only players whose lives all chain are compared. Players are labelled T{team} P{index}.
int RoundsFired(string matchId)
{
    var board = cache.LoadScoreboard(matchId) ?? throw new InvalidDataException("No scoreboard cached for this match.");
    var m = cache.LoadManifest(matchId);
    var header = cache.LoadChunk(matchId, FilmCache.ChunkOfType(m, 1));
    var named = Roster.Find(header, FilmHeader.Parse(header).StreamOffset).Where(r => r.Gamertag is not null).ToList();
    var walked = SummaryRecords.Walk(Summary.Payload(PacketReader.Read(cache.LoadChunk(matchId, FilmCache.ChunkOfType(m, 3)), out _))).Records;
    var (players, _) = FilmPlayers.Resolve(named, walked, board);
    var events = Summary.FromRecords(walked, players.Select(x => (string?)x.Gamertag).ToList());
    var deaths = events.Where(e => e.Kind == SummaryEventKind.Death).ToList();

    var pos = FilmPositions.Load(cache, matchId);
    var tracks = Lives.Tracks(pos.Bipeds);
    var matched = Lives.MatchDeaths(deaths.Select(d => pos.TickAt(d.TimeMs)).ToList(), tracks);
    var respawns = Lives.Respawns(deaths.Select(d => (double)d.TimeMs).ToList(), matched.Keys, tracks, pos.TickMs);
    var lives = Lives.Chain(deaths.Select(d => d.Player).ToList(), matched, respawns, tracks);

    // Every ammo count, per (slot, component), in tick order.
    var counts = new Dictionary<(int Slot, int Component), List<(int Tick, int Rounds, bool Checked)>>();
    var tick = 0;
    foreach (var c in m.CustomData.Chunks.Where(c => c.ChunkType == 2).OrderBy(c => c.Index))
        foreach (var p in PacketReader.Read(cache.LoadChunk(matchId, c), out _))
        {
            if (p.Type != 0) continue;
            foreach (var u in EntityRecords.FindBipedUpdates(p.Payload.Span))
                if (u.Rounds is { } r && (u.Checked || u.Components[0] == u.AmmoComponent))
                    (counts.TryGetValue((u.Slot, u.AmmoComponent!.Value), out var l) ? l : counts[(u.Slot, u.AmmoComponent!.Value)] = []).Add((tick, r, u.Checked));
            tick++;
        }
    var fullOf = counts.GroupBy(x => x.Key.Component).ToDictionary(g => g.Key, g => g.SelectMany(x => x.Value).Where(v => v.Checked).Select(v => v.Rounds).DefaultIfEmpty(0).Max());
    Console.WriteLine($"Largest checked count per component: {string.Join(", ", fullOf.OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Value}"))}");

    // A: checked drops only. B: plus unchecked counts that continue a run one round down.
    // C: B, plus the rounds before the first count after a rise or a life's start, up to 3, against the component's largest count.
    (int A, int B, int C) Count(List<(int Tick, int Rounds, bool Checked)> series, int full)
    {
        int a = 0, b = 0, cc = 0; int? lastChecked = null, last = null;
        foreach (var (_, r, chk) in series)
        {
            if (chk) { if (lastChecked is { } lc && r < lc) a += lc - r; lastChecked = r; }
            if (!chk && !(last is { } l0 && r == l0 - 1)) continue;
            if (last is { } l)
            {
                if (r < l) { b += l - r; cc += l - r; }
                else if (r > l && full - r is > 0 and <= 3) cc += full - r;
            }
            else if (full - r is > 0 and <= 3) cc += full - r;
            last = r;
        }
        return (a, b, cc);
    }

    Console.WriteLine("player   lives/spawns  shots fired   A checked   B +unchecked   C +refills");
    var ratios = new List<(double A, double B, double C)>();
    for (var i = 0; i < players.Count; i++)
    {
        var row = players[i].Row; var core = row.Stats?.CoreStats;
        var mine = lives.GetValueOrDefault(i) ?? [];
        if (core is null) continue;
        int sa = 0, sb = 0, sc = 0;
        foreach (var slot in mine)
            foreach (var (key, series) in counts.Where(x => x.Key.Slot == slot))
            { var (a, b, c) = Count(series, fullOf[key.Component]); sa += a; sb += b; sc += c; }
        var complete = mine.Count == core.Spawns;
        var shots = core.ShotsFired;
        if (complete && shots > 0) ratios.Add((sa / (double)shots, sb / (double)shots, sc / (double)shots));
        Console.WriteLine($"T{row.LastTeamId} P{i,-3}  {mine.Count,3}/{core.Spawns,-3}{(complete ? " " : "*")}    {shots,7}   {sa,9}   {sb,12}   {sc,10}");
        if (args.Contains("--slots")) Console.WriteLine($"         slots {string.Join(" ", mine)}");
    }
    double Med(IEnumerable<double> v) { var o = v.Order().ToList(); return o.Count == 0 ? double.NaN : o[o.Count / 2]; }
    Console.WriteLine($"Complete players: {ratios.Count}; median ratio to shots fired A {Med(ratios.Select(r => r.A)):F3}, B {Med(ratios.Select(r => r.B)):F3}, C {Med(ratios.Select(r => r.C)):F3}");
    Console.WriteLine($"  within 5%: A {ratios.Count(r => Math.Abs(r.A - 1) <= 0.05)}, B {ratios.Count(r => Math.Abs(r.B - 1) <= 0.05)}, C {ratios.Count(r => Math.Abs(r.C - 1) <= 0.05)}");
    return 0;
}

int Dump(string matchId, int index)
{
    var m = cache.LoadManifest(matchId);
    var c = m.CustomData.Chunks.FirstOrDefault(x => x.Index == index)
        ?? throw new ArgumentException($"This film has no chunk {index} (it has 0 to {m.CustomData.Chunks.Count - 1}).");
    var path = Path.Combine(cache.MatchDir(matchId), $"chunk{c.Index:D3}-type{c.ChunkType}.bin");
    File.WriteAllBytes(path, cache.LoadChunk(matchId, c));
    Console.WriteLine($"Wrote chunk {index} (type {c.ChunkType}) decompressed to {cache.DisplayRoot}/{matchId}/{Path.GetFileName(path)}");
    return 0;
}

int Inspect(string matchId)
{
    var m = cache.LoadManifest(matchId);
    var board = cache.LoadScoreboard(matchId);
    var chunks = m.CustomData.Chunks;
    var failures = 0;
    void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}"); if (!ok) failures++; }

    Console.WriteLine($"Match {matchId}");
    Console.WriteLine($"  film v{m.CustomData.FilmMajorVersion}, {chunks.Count} chunks, length {TimeSpan.FromMilliseconds(m.CustomData.FilmLength):mm\\:ss\\.fff}, " +
                      $"types {string.Join(", ", chunks.GroupBy(c => c.ChunkType).OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}"))}");

    // ---- Packet layer (type 2 and 3 chunks) ----
    Console.WriteLine("\nPacket layer");
    var packetTypes = new Dictionary<ushort, int>();
    long ticks = 0, tickSpanUs = 0;
    var exact = true; var monotonic = true; var worstDurationGapMs = 0L; var worstStartGapMs = 0L;
    ulong lastTs = 0; (long Ms, ulong Us)? firstStart = null;
    // Replication-stream data, checked after the summary is decoded (some checks need its events).
    var slotTables = new List<SlotTable>();
    var type6Ok = true; var type12Ok = true; var type8MaxCount = 0u;
    int tickMarkers = 0, tickLayoutMisses = 0, tickFollowedByType0 = 0, maxPresentSlots = 0;
    var tickData = new List<(double Ms, int Bytes)>();   // type-0 packets: film time and size
    var bipedCandidates = new List<(int Tick, EntityPosition Record)>();
    var objectCandidates = new List<(int Tick, EntityPosition Record)>();
    var checkedUpdates = new List<(int Tick, BipedUpdate Update)>();   // biped records whose command tick landed
    foreach (var c in chunks.Where(c => c.ChunkType != 1).OrderBy(c => c.Index))
    {
        var data = cache.LoadChunk(matchId, c);
        var packets = PacketReader.Read(data, out var consumed);
        exact &= consumed == data.Length;
        foreach (var p in packets) packetTypes[p.Type] = packetTypes.GetValueOrDefault(p.Type) + 1;
        if (c.ChunkType == 2 && packets.Count > 1)
        {
            foreach (var p in packets) { monotonic &= p.TimestampUs >= lastTs; lastTs = p.TimestampUs; }
            var span = (long)(packets[^1].TimestampUs - packets[0].TimestampUs);
            worstDurationGapMs = Math.Max(worstDurationGapMs, Math.Abs(span / 1000 - c.DurationMilliseconds));
            // Each chunk's first packet should sit at the manifest's start offset on the film's clock.
            firstStart ??= (c.ChunkStartTimeOffsetMilliseconds, packets[0].TimestampUs);
            var clockMs = (long)(packets[0].TimestampUs - firstStart.Value.Us) / 1000;
            worstStartGapMs = Math.Max(worstStartGapMs, Math.Abs(clockMs - (c.ChunkStartTimeOffsetMilliseconds - firstStart.Value.Ms)));
            var tickPackets = packets.Where(p => p.Type == 10).ToList();
            ticks += tickPackets.Count;
            if (tickPackets.Count > 1) tickSpanUs += (long)(tickPackets[^1].TimestampUs - tickPackets[0].TimestampUs);

            for (var i = 0; i < packets.Count; i++)
            {
                var p = packets[i];
                var span1 = p.Payload.Span;
                switch (p.Type)
                {
                    case 0:
                        var (found, objects) = EntityRecords.Find(span1);
                        bipedCandidates.AddRange(found.Select(r => (tickData.Count, r)));
                        objectCandidates.AddRange(objects.Select(r => (tickData.Count, r)));
                        checkedUpdates.AddRange(EntityRecords.FindBipedUpdates(span1).Where(u => u.Checked).Select(u => (tickData.Count, u)));
                        tickData.Add(((p.TimestampUs - firstStart.Value.Us) / 1000.0, p.Payload.Length));
                        break;
                    case 1:
                        slotTables.Add(SlotTable.Parse(p.Payload));
                        break;
                    case 6:
                        type6Ok &= span1.Length == 4 && !span1.ContainsAnyExcept((byte)0);
                        break;
                    case 8 when span1.Length >= 4:
                        type8MaxCount = Math.Max(type8MaxCount, BinaryPrimitives.ReadUInt32BigEndian(span1));
                        break;
                    case 12:
                        type12Ok &= span1.Length >= 4 && (span1.Length == 4) == (BinaryPrimitives.ReadUInt32BigEndian(span1) == 0);
                        break;
                    case 10:
                        tickMarkers++;
                        var (values, layoutOk) = TickMarker.Parse(p.Payload);
                        if (!layoutOk) tickLayoutMisses++;
                        else maxPresentSlots = Math.Max(maxPresentSlots, values.Count(v => v is not null));
                        if (i + 1 < packets.Count && packets[i + 1].Type == 0) tickFollowedByType0++;
                        break;
                }
            }
        }
    }
    Check(exact, "every type-2/3 chunk is a gap-free run of 16-byte-header packets");
    Check(monotonic, $"64-bit timestamps never go backwards across the whole film (clock at start: {(firstStart?.Us ?? 0) / 3.6e9:F2} h)");
    Check(worstDurationGapMs <= 50, $"packet timestamps (µs) span each chunk's manifest duration, worst gap {worstDurationGapMs} ms");
    Check(worstStartGapMs <= 50, $"each chunk starts at its manifest offset on the packet clock, worst gap {worstStartGapMs} ms");
    var hz = tickSpanUs == 0 ? 0 : ticks / (tickSpanUs / 1e6);
    Check(Math.Abs(hz - 60) < 3, $"type-10 tick packets arrive at {hz:F1} Hz");
    Console.WriteLine($"  packet types: {string.Join(", ", packetTypes.OrderBy(k => k.Key).Select(k => $"{k.Key}×{k.Value}"))}");

    // ---- Header chunk ----
    Console.WriteLine("\nHeader (type-1 chunk)");
    var headerInfo = FilmCache.ChunkOfType(m, 1);
    var header = cache.LoadChunk(matchId, headerInfo);
    var h = FilmHeader.Parse(header);
    Check(h.Version == m.CustomData.FilmMajorVersion, $"version field {h.Version} matches the manifest");
    Check(h.Blocks.Count > 0 && h.Blocks[0].Slots.Count == BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)),
        "block 0's used-slot count matches the count stored before it");
    Console.WriteLine($"  component registry: {h.Blocks.Count} blocks, {h.Blocks.Sum(b => b.Slots.Count)} slots, " +
                      $"{h.Blocks.SelectMany(b => b.Slots).Select(s => s.Name).Distinct().Count()} distinct names");
    Console.WriteLine($"  table after registry: {h.Table.Count} values");
    if (args.Contains("--registry"))
        foreach (var block in h.Blocks)
            for (var si = 0; si < block.Slots.Count; si++)
                Console.WriteLine($"    block {block.Index} slot {si}: {block.Slots[si].Name} ({block.Slots[si].Value})");
    Console.WriteLine($"  build: {h.Build.Version} / {h.Build.Branch} / {h.Build.Flavour} ({h.Build.Value1}, {h.Build.Value2})");

    var roster = Roster.Find(header, h.StreamOffset);
    var named = roster.Where(r => r.Gamertag is not null).ToList();
    Console.WriteLine($"  roster: {roster.Count} XUID-range values in the stream, {named.Count} with two matching name copies");
    var humans = board?.Players.Where(p => p.Xuid is not null).ToList() ?? [];
    if (board is not null)
    {
        var order = new List<long>();
        for (var i = 0; i < humans.Count; i++)
        {
            var e = named.FirstOrDefault(r => r.Xuid.ToString() == humans[i].Xuid);
            if (e is not null) order.Add(e.XuidBit);
            var name = e is null ? "" : showNames ? $": \"{e.Gamertag}\"" : $": name of {e.Gamertag!.Length} chars";
            // The header is written at the start, so players who joined later aren't in it.
            if (e is null && humans[i].ParticipationInfo?.JoinedInProgress == true)
                Console.WriteLine($"  [--] scoreboard player {i} (team {humans[i].LastTeamId}) joined in progress, not in the header");
            else
                Check(e is not null, $"scoreboard player {i} (team {humans[i].LastTeamId}) found with its gamertag{name}");
        }
        Check(order.SequenceEqual(order.Order()), "players appear in the header in scoreboard order");
        if (args.Contains("--roster"))
            foreach (var r in roster.Where(r => r.Gamertag is not null))
                Console.WriteLine($"    xuid bit {r.XuidBit,10}  name at +{r.NameBit - r.XuidBit,6}  on scoreboard: {(humans.Any(h => h.Xuid == r.Xuid.ToString()) ? "yes" : "no ")}  name of {r.Gamertag!.Length} chars");
        Console.WriteLine($"  XUID-range values with no name (mostly chance, see FORMAT.md): {roster.Count - named.Count}");
    }

    // ---- Summary chunk ----
    Console.WriteLine("\nSummary (type-3 chunk)");
    var summary = cache.LoadChunk(matchId, FilmCache.ChunkOfType(m, 3));
    var sp = PacketReader.Read(summary, out _);
    Console.WriteLine($"  packets: {string.Join(", ", sp.Select(p => $"type {p.Type} ({p.Payload.Length:N0} bytes)"))}");
    var payload = Summary.Payload(sp);

    // The summary walked by its layout (count, then records ending in u32 12000), without the roster.
    var (walked, storedCount, separatorsOk) = SummaryRecords.Walk(payload);
    Check(walked.Count == storedCount, $"walking the summary finds the stored record count ({walked.Count} of {storedCount})");
    Check(separatorsOk, "every summary record ends with the u32 12000");
    var rosterNames = named.Select(r => r.Gamertag).ToList();
    var bySearch = Summary.DecodeByNameSearch(payload, rosterNames);
    var walkedMine = Summary.FromRecords(walked, rosterNames);
    Check(walkedMine.Count == bySearch.Count && walkedMine.Zip(bySearch).All(x => x.First.NameBit == x.Second.NameBit && x.First.Player == x.Second.Player),
        "walking the summary by layout finds exactly the records the name search finds");

    // Whose records are whose: roster players by XUID, then late joiners linked by their counts.
    // Without a scoreboard, only the roster's records can be decoded.
    var (filmPlayers, unlinked) = board is null ? ([], []) : FilmPlayers.Resolve(named, walked, board);
    if (board is not null)
    {
        var lateJoiners = filmPlayers.Count(x => x.LateJoiner);
        Console.WriteLine($"  gamertags in the summary that aren't in the header: {lateJoiners + unlinked.Count} ({lateJoiners} linked to a late joiner on the scoreboard)");
        Check(unlinked.Count == 0, "each of those links to exactly one late joiner by kills, deaths and medals");
    }

    var events = Summary.FromRecords(walked, board is null ? rosterNames : filmPlayers.Select(x => (string?)x.Gamertag).ToList());
    var paired = Summary.PairKills(events);
    var kinds = events.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
    Console.WriteLine($"  events: {events.Count} ({string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}); " +
                      $"other codes {string.Join(", ", events.Where(e => e.Kind == SummaryEventKind.Other).GroupBy(e => e.Code).Select(g => $"{g.Key}×{g.Count()}"))}");
    Check(events.Zip(events.Skip(1)).All(x => x.Second.TimeMs >= x.First.TimeMs), "event times never go backwards");
    Check(events.Count == 0 || events[^1].TimeMs <= m.CustomData.FilmLength, $"last event ({events.LastOrDefault()?.TimeMs:N0} ms) is within the film");
    var kills = events.Count(e => e.Kind == SummaryEventKind.PlayerKill);
    Console.WriteLine($"  kills paired with the victim's death: {paired} of {kills}");

    if (board is not null)
    {
        // Scoreboard rows in event-player order: the roster (scoreboard order), then late joiners.
        var rows = filmPlayers.Select(x => x.Row).ToList();
        Check(filmPlayers.Count(x => !x.LateJoiner) == named.Count,
            $"every named roster entry is on the scoreboard ({filmPlayers.Count(x => !x.LateJoiner)} of {named.Count})");
        int Count(int p, SummaryEventKind k) => events.Count(e => e.Player == p && e.Kind == k);
        bool all(Func<int, bool> f) => Enumerable.Range(0, rows.Count).All(f);
        int PvpKills(MatchPlayer pl) =>
            pl.Stats?.ModeStats.TryGetValue("PvpStats", out var pvp) == true && pvp.TryGetProperty("Kills", out var k) ? k.GetInt32() : 0;
        Check(all(p => Count(p, SummaryEventKind.Death) == rows[p].Stats?.CoreStats.Deaths), "deaths match the scoreboard for every player");
        Check(all(p => Count(p, SummaryEventKind.PlayerKill) == PvpKills(rows[p])), "player kills match the scoreboard's PvP kills for every player");
        Check(all(p => Count(p, SummaryEventKind.Medal) == rows[p].Stats?.CoreStats.Medals.Sum(x => x.Count)), "medal counts match the scoreboard for every player");
        // Players with no decodable records: bots (their deaths aren't recorded at all) and any late
        // joiner who couldn't be linked. Kills of them go unpaired; deaths they caused have no killer.
        var outsiders = board.Players.Except(rows).ToList();
        int Core(MatchPlayer pl, Func<CoreStats, int> f) => pl.Stats is { } s ? f(s.CoreStats) : 0;
        var outsiderDeaths = outsiders.Sum(o => Core(o, c => c.Deaths) - Core(o, c => c.Suicides));
        var outsiderKills = outsiders.Sum(o => Core(o, c => c.Kills));
        Check(kills - paired == outsiderDeaths,
            $"kills without a victim record ({kills - paired}) match deaths of bots and unlinked players, less their suicides ({outsiderDeaths})");
        var noKiller = events.Where(d => d.Kind == SummaryEventKind.Death && !events.Any(k => k.Victim == d.Player && k.TimeMs <= d.TimeMs && d.TimeMs - k.TimeMs <= 2))
            .GroupBy(d => d.Player).ToDictionary(g => g.Key, g => g.Count());
        // Only meaningful when every death is to a player; in Firefight most deaths are to AI.
        if (rows.All(r => r.Stats?.ModeStats.ContainsKey("PvpStats") == true))
        {
            Check(all(p => noKiller.GetValueOrDefault(p) >= rows[p].Stats?.CoreStats.Suicides),
                "every player has at least as many deaths with no killer as scoreboard suicides");
            var suicides = rows.Sum(r => Core(r, c => c.Suicides));
            Check(noKiller.Values.Sum() == suicides + outsiderKills,
                $"deaths with no killer ({noKiller.Values.Sum()}) = suicides ({suicides}) + kills by bots and unlinked players ({outsiderKills})");
        }

        // Medal identities, for codes already known.
        var knownOk = true; var unknownCodes = new HashSet<int>();
        for (var p = 0; p < rows.Count; p++)
        {
            var want = rows[p].Stats!.CoreStats.Medals.ToDictionary(x => x.NameId, x => x.Count);
            var got = new Dictionary<long, int>();
            foreach (var e in events.Where(e => e.Player == p && e.Kind == SummaryEventKind.Medal))
                if (MedalCodes.Known.TryGetValue(e.MedalCode, out var id)) got[id] = got.GetValueOrDefault(id) + 1; else unknownCodes.Add(e.MedalCode);
            knownOk &= got.All(g => want.GetValueOrDefault(g.Key) == g.Value);
        }
        Check(knownOk, "every medal with a known code matches the scoreboard's medal and count");

        // A medal record's event code is 25/50/75 for normal/heroic/legendary medals, and a fixed
        // value per medal from 100 up for mythic ones (Overkill 110, Killtacular 112, ...).
        if (cache.LoadMedals()?.Medals.ToDictionary(x => x.NameId) is { } medalMeta)
        {
            // Only medals the cached metadata knows; a newer medal is skipped rather than failing.
            var withMeta = events.Where(e => e.Kind == SummaryEventKind.Medal
                && MedalCodes.Known.TryGetValue(e.MedalCode, out var id) && medalMeta.ContainsKey(id)).ToList();
            bool Fits(SummaryEvent e) => medalMeta[MedalCodes.Known[e.MedalCode]].DifficultyIndex is var d && d < 3 ? e.Code == 25 * (d + 1) : e.Code >= 100;
            var fitting = withMeta.Count(Fits);
            Check(fitting == withMeta.Count, $"each known medal's event code fits its difficulty ({fitting} of {withMeta.Count})");
            Check(withMeta.GroupBy(e => e.MedalCode).All(g => g.Select(e => e.Code).Distinct().Count() == 1), "each medal always has the same event code");
        }
        Console.WriteLine($"  medal codes not in the table yet: {(unknownCodes.Count == 0 ? "none" : string.Join(", ", unknownCodes.Order()))}");

        if (args.Contains("--learn"))
        {
            // A code is learnable when exactly one scoreboard medal has the same count for every player.
            // With the medal metadata cached (fetch saves it), each candidate is named and its
            // difficulty compared with the event code: 25/50/75/100 for normal/heroic/legendary/mythic.
            var meta = cache.LoadMedals()?.Medals.ToDictionary(x => x.NameId);
            string Describe(long id) => meta?.GetValueOrDefault(id) is { } d ? $"{id} {d.Name} (difficulty code {25 * (d.DifficultyIndex + 1)})" : $"{id}";
            var ids = rows.SelectMany(r => r.Stats!.CoreStats.Medals.Select(x => x.NameId)).Distinct().ToList();
            int[] Vec(Func<int, int> f) => Enumerable.Range(0, rows.Count).Select(f).ToArray();
            foreach (var code in unknownCodes.Order())
            {
                var mine = events.Where(e => e.Kind == SummaryEventKind.Medal && e.MedalCode == code).ToList();
                var v = Vec(p => mine.Count(e => e.Player == p));
                var fits = ids.Where(id => Vec(p => rows[p].Stats!.CoreStats.Medals.Where(x => x.NameId == id).Sum(x => x.Count)).SequenceEqual(v)).ToList();
                var codes = string.Join("/", mine.Select(e => e.Code).Distinct().Order());
                Console.WriteLine($"    code {code,3} (event code {codes}, {mine.Count}×): " +
                                  (fits.Count == 1 ? $"medal {Describe(fits[0])}" : $"{fits.Count} medals fit: {string.Join("; ", fits.Select(Describe))}"));
            }
        }

        // Who killed whom, as a check on the pairing (team kills would be betrayals).
        var crossTeam = events.Where(e => e.Victim is not null).All(e => rows[e.Player].LastTeamId != rows[e.Victim!.Value].LastTeamId
            || rows[e.Player].Stats?.CoreStats.Betrayals > 0);
        Check(crossTeam, "every victim is on the other team (unless the killer has betrayals)");

        // Objective events (code 5): in CTF, one per flag steal, capture, capture assist, return and
        // carrier kill, plus some flag pickups; in Firefight, a 5-second award for holding an objective.
        var objectives = events.Where(e => e.Kind == SummaryEventKind.Objective).ToList();
        int Ctf(MatchPlayer pl, string stat) =>
            pl.Stats?.ModeStats.TryGetValue("CaptureTheFlagStats", out var ctf) == true && ctf.TryGetProperty(stat, out var v) ? v.GetInt32() : 0;
        if (rows.Any(r => r.Stats?.ModeStats.ContainsKey("CaptureTheFlagStats") == true))
        {
            string[] counted = ["FlagSteals", "FlagCaptures", "FlagCaptureAssists", "FlagReturns", "FlagCarriersKilled"];
            var extras = Enumerable.Range(0, rows.Count).Select(p => Count(p, SummaryEventKind.Objective) - counted.Sum(s => Ctf(rows[p], s))).ToList();
            Check(extras.All(x => x is 0 or 1) && Enumerable.Range(0, rows.Count).All(p => extras[p] == 0 || Ctf(rows[p], "FlagGrabs") > 0 && Ctf(rows[p], "FlagSecures") > 0),
                $"each player's objective events = flag steals + captures + capture assists + returns + carrier kills, or 1 more for a player with grabs and secures (extras {string.Join(" ", extras)})");
            // A carrier kill logs an objective event at the killer's own kill, at the same moment.
            var atOwnKill = Enumerable.Range(0, rows.Count).Select(p => objectives.Count(o => o.Player == p
                && events.Any(k => k.Player == p && k.Kind == SummaryEventKind.PlayerKill && Math.Abs((long)k.TimeMs - o.TimeMs) <= 5))).ToList();
            Check(Enumerable.Range(0, rows.Count).All(p => atOwnKill[p] <= Ctf(rows[p], "FlagCarriersKilled")),
                $"objective events at the player's own kill never exceed their flag carrier kills ({atOwnKill.Sum()} against {rows.Sum(r => Ctf(r, "FlagCarriersKilled"))})");
        }
        Check(objectives.All(o => !events.Any(k => k.Kind == SummaryEventKind.PlayerKill && k.Player != o.Player && Math.Abs((long)k.TimeMs - o.TimeMs) <= 5)),
            "an objective event never shares a moment (5 ms) with another player's kill");
        if (board.MatchInfo.GameVariantCategory == GameVariantFirefight && objectives.Count > 1)
        {
            // Firefight: bursts of 5-second awards, one burst per objective completed.
            var sorted = objectives.OrderBy(o => o.TimeMs).ToList();
            var bursts = 1 + sorted.Zip(sorted.Skip(1)).Count(x => x.Second.TimeMs - x.First.TimeMs > 20_000);
            Check(rows.All(r => r.Stats?.CoreStats.ObjectivesCompleted == bursts), $"objective events come in {bursts} bursts, as many as each player's objectives completed");
        }
    }

    // Every non-medal record's +472 byte is its record type: 0 kill, 1 death, 2 objective.
    var badTypes = walked.Where(r => !r.IsMedal && Summary.RecordTypeOf(r.Code) != r.TypeByte).GroupBy(r => (r.Code, r.TypeByte)).ToList();
    Check(badTypes.Count == 0, "every non-medal record's +472 byte is its code's record type (kill 0, death 1, objective 2), and no other codes occur" +
        (badTypes.Count == 0 ? "" : $": found {string.Join(", ", badTypes.Select(g => $"code {g.Key.Code} type {g.Key.TypeByte} ×{g.Count()}"))}"));

    InspectReplication(Check, slotTables, events, board, humans.Count,
        (type6Ok, type12Ok, type8MaxCount), (tickMarkers, tickLayoutMisses, tickFollowedByType0, maxPresentSlots), tickData);
    InspectBipedUpdates(Check, checkedUpdates);
    if (board is not null && board.MatchInfo.GameVariantCategory != GameVariantFirefight)
        InspectPositions(Check, events, filmPlayers.Select(x => x.Row.LastTeamId).ToList(),
            filmPlayers.Select(x => x.Row.Stats?.CoreStats.Spawns ?? 0).ToList(), tickData,
            EntityRecords.FilterBipeds(bipedCandidates, objectCandidates), objectCandidates);

    Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
    return failures == 0 ? 0 : 2;
}
