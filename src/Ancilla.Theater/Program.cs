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

int Help()
{
    Console.WriteLine("""
        Ancilla.Theater fetch [matchId]        download a film (default: your latest matchmade match); needs HALO_SPARTAN_TOKEN
                              --pvp          latest matchmade match that isn't Firefight
                              --count=N      the latest N (of your last 25), one per mode category first
        Ancilla.Theater list                   films in the cache
        Ancilla.Theater inspect [matchId]      decode what is understood so far and check it (default: newest cached)
                                --show-names   include gamertags in the roster output
                                --learn        propose medals for summary medal codes not in the table yet
        Ancilla.Theater timeline [matchId]     kill feed, score flow, head-to-head and per-player lives (default: newest cached)
                                --show-names   label players with gamertags instead of T0 P1
                                --no-feed      leave out the kill feed
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

    var countArg = args.FirstOrDefault(a => a.StartsWith("--count=", StringComparison.Ordinal));
    var count = countArg is null ? 1 : ParseNumber(countArg["--count=".Length..], "--count value");
    var me = await client.GetMeAsync();
    var history = await client.GetMatchHistoryAsync(me.Xuid, 0, 25, MatchHistoryType.Matchmaking);
    // --pvp skips Firefight (mode category 42).
    var candidates = history.Results
        .Where(r => !args.Contains("--pvp") || r.MatchInfo.GameVariantCategory != GameVariantFirefight)
        .Where(r => HaloIds.IsGuid(r.MatchId))
        .ToList();
    // Newest match of each mode category first, then the rest newest first, so a batch covers more modes.
    var firstOfEach = candidates.GroupBy(r => r.MatchInfo.GameVariantCategory).Select(g => g.First()).ToList();
    var picks = firstOfEach.Concat(candidates.Except(firstOfEach)).Take(count).ToList();
    if (picks.Count == 0) throw new InvalidDataException("No matching matchmade match in your last 25.");

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

string ResolveMatch(string? matchId) =>
    matchId is not null
        ? HaloIds.RequireGuid(matchId, nameof(matchId))
        : cache.NewestMatch() ?? throw new InvalidDataException("No films cached. Run 'Ancilla.Theater fetch' first.");

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
    }

    Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
    return failures == 0 ? 0 : 2;
}
