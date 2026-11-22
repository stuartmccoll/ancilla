using System.Collections.Concurrent;
using Ancilla.Core;
using Ancilla.Data;
using Ancilla.Theater;
using Microsoft.Extensions.Configuration;

namespace Ancilla.UI.Services;

public sealed record MedalBook(MedalMetadata Meta, MedalSprite Sprite, string SheetUrl, IReadOnlyDictionary<long, MedalDefinition> ById)
{
    public string TypeOf(MedalDefinition m) => m.TypeIndex >= 0 && m.TypeIndex < Meta.Types.Count ? Meta.Types[m.TypeIndex] : "";
    public string DifficultyOf(MedalDefinition m) =>
        m.DifficultyIndex >= 0 && m.DifficultyIndex < Meta.Difficulties.Count ? Meta.Difficulties[m.DifficultyIndex] : "";
}

public sealed record ModeRecord(int Category, ServiceRecord Record);

public sealed record RankedPlaylist(string PlaylistId, string Name, PlaylistCsr Csr);

/// <summary>A history entry with its full scoreboard.</summary>
public sealed record RecentMatch(MatchHistoryEntry Entry, MatchStats Stats)
{
    /// <summary>Firefight and other games against AI only (kills there aren't comparable to PvP).</summary>
    public bool IsPve(string xuid) => Stats.IsPve(xuid);
}

/// <summary>A row in the match list, filled in progressively as its details arrive.</summary>
public sealed class MatchRow(MatchHistoryEntry entry)
{
    public MatchHistoryEntry Entry { get; } = entry;
    public UgcAsset? Map { get; set; }
    public UgcAsset? Mode { get; set; }
    public UgcAsset? Playlist { get; set; }
    public CoreStats? Mine { get; set; }
    public string? ScoreLine { get; set; }
    public bool Loaded { get; set; }
}

public sealed class MatchListState
{
    public MatchHistoryType Type { get; init; } = MatchHistoryType.Matchmaking;
    public List<MatchRow> Rows { get; } = [];
    public int NextStart { get; set; }
    public bool HasMore { get; set; } = true;
    public bool Loading { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Per-browser-session state (Blazor Server scopes services to the circuit): the signed-in
/// player's client and their data. Data that's the same for everyone and never changes
/// (finished matches, maps, medals) goes through the shared <see cref="HaloCache"/>.
/// The token lives only inside the client object, in server memory.
/// </summary>
public sealed class HaloSession(IHttpClientFactory httpFactory, HaloCache cache, AssetCache assets, FilmCache films, IConfiguration config, HaloDiagnostics diagnostics,
    ArchiveStore archive, SnapshotStore legacySnapshots)
{
    private readonly ConcurrentDictionary<string, Task<MatchTimeline>> _timelines = new();
    // The ranked-playlist list isn't discoverable by name without a version ID, so seed the common one.
    private static readonly Dictionary<string, string> KnownPlaylists = new()
    {
        ["edfef3ac-9cbe-4fa2-b949-8f29deafd483"] = "Ranked Arena",
    };

    private readonly ConcurrentDictionary<string, UserProfile> _users = new();
    private readonly ConcurrentDictionary<string, string> _playlistNames = new(KnownPlaylists);
    private const int MaxParallel = 8;
    private readonly SemaphoreSlim _throttle = new(MaxParallel);
    private int _waiting;
    private readonly SemaphoreSlim _historyLock = new(1);
    // History lists come from the archive. _fresh: lists already checked for new matches this session.
    // _refresh: lists the user asked to re-check (see ForgetHistory).
    private readonly HashSet<MatchHistoryType> _fresh = [];
    private readonly HashSet<MatchHistoryType> _refresh = [];
    private HaloWaypointClient? _client;
    private Task<ServiceRecord>? _serviceRecord;
    // Filtered service records, keyed by (category, isRanked) for raw API calls.
    private readonly ConcurrentDictionary<(int?, bool?), Task<ServiceRecord>> _filteredRecords = new();
    private Task<IReadOnlyList<RankedPlaylist>>? _ranked;
    public const string UnnamedPlaylist = "Ranked playlist";

    public UserProfile? Me { get; private set; }
    public string? ServiceTag { get; private set; }
    public bool FromEnvironment { get; private set; }
    public bool IsSignedIn => Me is not null;

    public ProgressReport? Report { get; set; }
    /// <summary>When the service record was last fetched.</summary>
    public DateTimeOffset? ServiceRecordFetchedAt { get; private set; }
    /// <summary>The Stats page's filter, kept for this session.</summary>
    public StatsFilter StatsFilter { get; set; } = new();
    /// <summary>The Medals page's own filter, kept for this session.</summary>
    public StatsFilter MedalsFilter { get; set; } = new();
    /// <summary>The XP-per-match window picked on the Progress page, kept for this session.</summary>
    public XpAverageWindow XpAverage { get; set; } = XpAverageWindow.Default;
    /// <summary>A manual XP-per-match figure, which overrides <see cref="XpAverage"/> when set.</summary>
    public double? XpOverride { get; set; }
    public MatchListState Matches { get; private set; } = new();

    /// <summary>Pass as the count to <see cref="GetRecentMatchesAsync"/> for everything in the archive, with no API paging.</summary>
    public const int AllStored = int.MaxValue;
    /// <summary>How 429 answers are backed off. Tests turn the waits down.</summary>
    public RetryPolicy Retry { get; set; } = RetryPolicy.Default;
    /// <summary>The background check for new matches that most recently started, for tests to wait on.</summary>
    public Task? BackgroundSync { get; private set; }
    /// <summary>The signed-in player's progress snapshots, kept in the archive.</summary>
    public ISnapshotStore Snapshots => archive.SnapshotsFor(Xuid);

    /// <summary>Raised on sign-in/out.</summary>
    public event Action? Changed;
    /// <summary>Raised (possibly off the UI thread) whenever match rows load or fill in.</summary>
    public event Action? MatchesChanged;

    /// <summary>Requests waiting for one of the 8 slots, for the Diagnostics page.</summary>
    public int QueueLength => Volatile.Read(ref _waiting);
    /// <summary>Requests running right now.</summary>
    public int InFlight => MaxParallel - _throttle.CurrentCount;

    private async Task EnterThrottleAsync()
    {
        Interlocked.Increment(ref _waiting);
        try { await _throttle.WaitAsync(); }
        finally { Interlocked.Decrement(ref _waiting); }
    }

    public HaloWaypointClient Client => _client ?? throw new InvalidOperationException("Not signed in.");
    public string Xuid => Me?.Xuid ?? throw new InvalidOperationException("Not signed in.");

    // ---- Sign-in -------------------------------------------------------------------

    public async Task<bool> TrySignInFromEnvironmentAsync()
    {
        var token = config["HALO_SPARTAN_TOKEN"];
        if (string.IsNullOrWhiteSpace(token)) return false;
        await SignInAsync(token);
        FromEnvironment = true;
        return true;
    }

    public async Task SignInAsync(string token)
    {
        var client = new HaloWaypointClient(httpFactory.CreateClient(), token, diagnostics);
        var me = await client.GetMeAsync();   // validates the token
        Reset();
        _client = client;
        Me = me;
        diagnostics.Redactor.Register(me.Gamertag);
        diagnostics.Redactor.Register(me.Xuid);
        diagnostics.Info("Signed in");
        try { await archive.ImportSnapshotsAsync(legacySnapshots, me.Xuid); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            diagnostics.Info("Couldn't import the old snapshots file");   // it stays where it was; the next sign-in tries again
        }
        try { ServiceTag = (await client.GetAppearanceAsync(me.Xuid)).Appearance?.ServiceTag; }
        catch (HttpRequestException) { /* cosmetic only */ }
        Changed?.Invoke();
    }

    public void SignOut()
    {
        Reset();
        Changed?.Invoke();
    }

    private void Reset()
    {
        _client = null;
        Me = null;
        ServiceTag = null;
        FromEnvironment = false;
        Report = null;
        _fresh.Clear();
        _refresh.Clear();
        _serviceRecord = null;
        _filteredRecords.Clear();
        _ranked = null;
        Matches = new();
    }

    // ---- Player data (per session, refreshable) ----------------------------------------

    public Task<ServiceRecord> GetServiceRecordAsync(bool refresh = false)
    {
        if (refresh || _serviceRecord is null || _serviceRecord.IsFaulted)
        {
            _serviceRecord = Client.GetServiceRecordAsync(Xuid);
            ServiceRecordFetchedAt = DateTimeOffset.Now;
            _ranked = null;
        }
        return _serviceRecord;
    }

    /// <summary>
    /// The service record narrowed by <paramref name="filter"/>. PvP is everything minus Firefight,
    /// because the API can filter to one mode category but not exclude one.
    /// </summary>
    public async Task<ServiceRecord> GetServiceRecordAsync(StatsFilter filter, bool refresh = false)
    {
        if (refresh)
        {
            _filteredRecords.Clear();
            _ = GetServiceRecordAsync(true);   // also refreshes ranked CSR, which hangs off the full record
        }
        if (filter == StatsFilter.Everything) return await GetServiceRecordAsync();
        var ranked = filter.IsRanked;
        return filter.Scope switch
        {
            StatsScope.All => await RawRecord(null, ranked),
            StatsScope.Firefight => await RawRecord(GameVariantCategories.Firefight, ranked),
            _ => ServiceRecordMath.Subtract(
                ranked is null ? await GetServiceRecordAsync() : await RawRecord(null, ranked),
                await RawRecord(GameVariantCategories.Firefight, ranked),
                "PveStats"),
        };
    }

    /// <summary>
    /// One record per game mode in <paramref name="filter"/>, for modes with matches. Cached with
    /// the other filtered records, so <see cref="GetServiceRecordAsync(StatsFilter, bool)"/> with
    /// refresh clears them too.
    /// </summary>
    public async Task<IReadOnlyList<ModeRecord>> GetModeRecordsAsync(StatsFilter filter)
    {
        IEnumerable<int> categories = filter.Scope switch
        {
            StatsScope.Firefight => [GameVariantCategories.Firefight],
            StatsScope.PvP => GameVariantCategories.PvP,
            _ => [.. GameVariantCategories.PvP, GameVariantCategories.Firefight],
        };
        var results = await Task.WhenAll(categories.Select(async c =>
        {
            await EnterThrottleAsync();
            try { return new ModeRecord(c, await RawRecord(c, filter.IsRanked)); }
            catch (HttpRequestException) { return null; }   // one missing mode shouldn't hide the rest
            finally { _throttle.Release(); }
        }));
        return results.OfType<ModeRecord>().Where(m => m.Record.MatchesCompleted > 0).ToList();
    }

    private Task<ServiceRecord> RawRecord(int? category, bool? isRanked)
    {
        var key = (category, isRanked);
        if (_filteredRecords.TryGetValue(key, out var t) && !t.IsFaulted) return t;
        return _filteredRecords[key] = Client.GetServiceRecordAsync(Xuid, category, isRanked);
    }

    /// <summary>CSR for every playlist in the service record that turns out to be ranked.</summary>
    public Task<IReadOnlyList<RankedPlaylist>> GetRankedAsync()
    {
        if (_ranked is null || _ranked.IsFaulted) _ranked = LoadRankedAsync();
        return _ranked;
    }

    private async Task<IReadOnlyList<RankedPlaylist>> LoadRankedAsync()
    {
        var sr = await GetServiceRecordAsync();
        var ids = (sr.Subqueries?.PlaylistAssetIds ?? []).Concat(KnownPlaylists.Keys).Where(HaloIds.IsGuid).Distinct().ToList();
        var client = Client;
        var xuid = Xuid;
        var results = await Task.WhenAll(ids.Select(async id =>
        {
            await EnterThrottleAsync();
            try
            {
                var r = await client.GetPlaylistCsrAsync(id, xuid);
                var csr = r.Value.FirstOrDefault(v => v.ResultCode == 0)?.Result;
                return csr is null ? null : new RankedPlaylist(id, _playlistNames.GetValueOrDefault(id, UnnamedPlaylist), csr);
            }
            catch (HttpRequestException) { return null; }   // unranked playlists answer 400/404
            finally { _throttle.Release(); }
        }));
        var ranked = results.OfType<RankedPlaylist>().ToList();

        // Playlist names can only be looked up with a version ID, which comes from match history.
        // Check the most recent matchmade games (pages the Matches section shares) for unnamed ones.
        var unnamed = ranked.Where(r => r.Name == UnnamedPlaylist).Select(r => r.PlaylistId).ToHashSet();
        if (unnamed.Count > 0)
        {
            try
            {
                var refs = (await GetHistoryAsync(MatchHistoryType.Matchmaking, 100))
                    .Select(e => e.MatchInfo.Playlist)
                    .Where(p => p is not null && unnamed.Contains(p.AssetId))
                    .DistinctBy(p => p!.AssetId);
                await Task.WhenAll(refs.Select(p => GetPlaylistAsync(p)));
                ranked = ranked.Select(r => r with { Name = _playlistNames.GetValueOrDefault(r.PlaylistId, UnnamedPlaylist) }).ToList();
            }
            catch (HttpRequestException) { }   // names are a nicety
        }
        return ranked;
    }

    /// <summary>
    /// The most recent <paramref name="count"/> matches with their scoreboards, newest first. They come
    /// from the local archive, and only what it's missing is fetched: after a restart, matches already
    /// stored need no API calls (new ones are looked for in the background; call
    /// <see cref="ForgetHistory"/> to wait for that instead). <see cref="AllStored"/> returns everything
    /// stored, using the API only to fill an empty archive. <paramref name="progress"/> gets (done, total).
    /// </summary>
    public async Task<IReadOnlyList<RecentMatch>> GetRecentMatchesAsync(MatchHistoryType type, int count, Action<int, int>? progress = null)
    {
        var storedOnly = count == AllStored;
        var entries = await GetHistoryAsync(type, count, storedOnly);
        var done = 0;
        var results = await Task.WhenAll(entries.Select(async e =>
        {
            await EnterThrottleAsync();
            try { return new RecentMatch(e, await GetMatchAsync(e.MatchId)); }
            catch (HttpRequestException) { return null; }
            finally
            {
                _throttle.Release();
                progress?.Invoke(Interlocked.Increment(ref done), entries.Count);
            }
        }));
        return results.OfType<RecentMatch>().ToList();
    }

    /// <summary>Skill results (expected kills/deaths, CSR) for several matches, keyed by match ID.</summary>
    public async Task<IReadOnlyDictionary<string, MatchSkill>> GetSkillsAsync(IEnumerable<string> matchIds)
    {
        var pairs = await Task.WhenAll(matchIds.Distinct().Select(async id =>
        {
            await EnterThrottleAsync();
            try { return (id, skill: await GetSkillAsync(id)); }
            finally { _throttle.Release(); }
        }));
        return pairs.Where(p => p.skill is not null).ToDictionary(p => p.id, p => p.skill!);
    }

    /// <summary>Makes the next load check the API for new matches and wait for the answer, rather than doing it in the background.</summary>
    public void ForgetHistory()
    {
        _fresh.Clear();
        _refresh.UnionWith(Enum.GetValues<MatchHistoryType>());
    }

    private const int PageSize = 25;

    /// <summary>
    /// The newest <paramref name="count"/> history entries from the archive. Anything the archive lacks is
    /// paged in from the API first: new matches (found by paging from the top until a known one turns up),
    /// then older ones, which is also how <see cref="BackfillAsync"/> reaches past the first 200.
    /// <paramref name="storedOnly"/> never pages older matches, so it can't walk the whole history.
    /// </summary>
    private async Task<List<MatchHistoryEntry>> GetHistoryAsync(MatchHistoryType type, int count, bool storedOnly = false,
        Action<int>? progress = null, CancellationToken ct = default)
    {
        var xuid = Xuid;
        var client = Client;
        await _historyLock.WaitAsync(ct);   // two pages loading at once mustn't interleave pages
        try
        {
            var stored = archive.CountHistory(xuid, type);
            if (!_fresh.Contains(type))
            {
                var mustWait = _refresh.Contains(type) || stored == 0 || (!storedOnly && stored < count && !archive.IsComplete(xuid, type));
                _fresh.Add(type);
                if (mustWait)
                {
                    _refresh.Remove(type);
                    await SyncNewAsync(client, xuid, type, storedOnly ? PageSize : count, ct);
                }
                else
                {
                    BackgroundSync = Task.Run(() => SyncInBackgroundAsync(client, xuid, type), CancellationToken.None);
                }
            }
            while (!storedOnly && !archive.IsComplete(xuid, type) && (stored = archive.CountHistory(xuid, type)) < count)
            {
                ct.ThrowIfCancellationRequested();
                var page = await Retry.RunAsync(() => client.GetMatchHistoryAsync(xuid, stored, PageSize, type, ct), ct);
                archive.PutHistory(xuid, type, page.Results);
                if (page.ResultCount < PageSize) archive.SetComplete(xuid, type, true);
                progress?.Invoke(archive.CountHistory(xuid, type));
                if (page.ResultCount == 0) break;
            }
            return archive.GetHistory(xuid, type, count).ToList();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    /// <summary>
    /// Pages from the newest match until one the archive already has turns up, or the end of history. An
    /// empty archive stops once <paramref name="limit"/> matches are in, so a first load isn't the whole history.
    /// </summary>
    private async Task SyncNewAsync(HaloWaypointClient client, string xuid, MatchHistoryType type, int limit, CancellationToken ct)
    {
        var wasEmpty = archive.CountHistory(xuid, type) == 0;
        var fetched = 0;
        for (var start = 0; ; start += PageSize)
        {
            var page = await Retry.RunAsync(() => client.GetMatchHistoryAsync(xuid, start, PageSize, type, ct), ct);
            var known = page.Results.Any(e => archive.HasHistory(xuid, type, e.MatchId));
            archive.PutHistory(xuid, type, page.Results);
            fetched += page.Results.Count;
            if (page.ResultCount < PageSize) { archive.SetComplete(xuid, type, true); return; }
            if (known || (wasEmpty && fetched >= limit)) return;
        }
    }

    private async Task SyncInBackgroundAsync(HaloWaypointClient client, string xuid, MatchHistoryType type)
    {
        try
        {
            await _historyLock.WaitAsync();
            try { await SyncNewAsync(client, xuid, type, PageSize, CancellationToken.None); }
            finally { _historyLock.Release(); }

            // Scoreboards for what turned up, so the next load has them. The newest few are enough: older
            // gaps are what Backfill is for.
            var missing = archive.GetHistory(xuid, type, 50).Where(e => !archive.HasMatch(e.MatchId)).ToList();
            await Task.WhenAll(missing.Select(async e =>
            {
                await EnterThrottleAsync();
                try { await GetMatchAsync(e.MatchId); }
                catch (Exception) { /* fetched when it's asked for */ }
                finally { _throttle.Release(); }
            }));
        }
        catch (Exception) { /* a background check that fails (offline, signed out) just tries again next session */ }
        finally { MatchesChanged?.Invoke(); }
    }

    /// <summary>What <see cref="BackfillAsync"/> found.</summary>
    public sealed record BackfillResult(int Matches, int Added, int ScoreboardsFetched);

    /// <summary>
    /// Pages the whole match history into the archive, then fetches the scoreboard (and, for matchmade
    /// games, the skill result) of every stored match that lacks one. Safe to run again: it only
    /// fetches what's missing. <paramref name="progress"/> describes what it's doing now.
    /// </summary>
    public async Task<BackfillResult> BackfillAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        var xuid = Xuid;
        var before = archive.CountHistory(xuid, MatchHistoryType.All);
        foreach (var type in new[] { MatchHistoryType.Matchmaking, MatchHistoryType.All })
            await GetHistoryAsync(type, AllStored, storedOnly: false,
                n => progress?.Invoke($"Found {n:N0} {(type == MatchHistoryType.All ? "matches" : "matchmade matches")}…"), ct);

        var missing = archive.GetHistory(xuid, MatchHistoryType.All).Where(e => !archive.HasMatch(e.MatchId)).ToList();
        var done = 0;
        var fetched = 0;
        await Task.WhenAll(missing.Select(async e =>
        {
            await EnterThrottleAsync();
            try
            {
                ct.ThrowIfCancellationRequested();
                await GetMatchAsync(e.MatchId);
                Interlocked.Increment(ref fetched);
            }
            catch (HttpRequestException) { /* gone from the service; skip it */ }
            finally
            {
                _throttle.Release();
                progress?.Invoke($"Loaded {Interlocked.Increment(ref done):N0} of {missing.Count:N0} scoreboards…");
            }
        }));

        var ranked = archive.GetHistory(xuid, MatchHistoryType.Matchmaking).Select(e => e.MatchId)
            .Where(id => !archive.TryGetSkill(xuid, id, out _)).ToList();
        done = 0;
        await Task.WhenAll(ranked.Select(async id =>
        {
            await EnterThrottleAsync();
            try { ct.ThrowIfCancellationRequested(); await GetSkillAsync(id); }
            finally
            {
                _throttle.Release();
                progress?.Invoke($"Loaded {Interlocked.Increment(ref done):N0} of {ranked.Count:N0} skill results…");
            }
        }));

        var after = archive.CountHistory(xuid, MatchHistoryType.All);
        return new BackfillResult(after, after - before, fetched);
    }

    // ---- The archive ----------------------------------------------------------------------

    /// <summary>What's stored for the signed-in player.</summary>
    public ArchiveSummary ArchiveSummary() => archive.Summary(Xuid);

    /// <summary>Where the archive file is, with the home folder shown as ~.</summary>
    public string ArchiveFile => archive.DisplayPath;

    /// <summary>The player's stored matches as flat rows, for export.</summary>
    public IReadOnlyList<ArchiveRow> ExportRows() => archive.Rows(Xuid);

    public void SetMatchType(MatchHistoryType type)
    {
        Matches = new MatchListState { Type = type };
        MatchesChanged?.Invoke();
    }

    public async Task LoadMoreMatchesAsync()
    {
        var s = Matches;
        if (s.Loading || !s.HasMore) return;
        s.Loading = true;
        s.Error = null;
        MatchesChanged?.Invoke();
        try
        {
            var page = await Client.GetMatchHistoryAsync(Xuid, s.NextStart, 25, s.Type);
            var rows = page.Results.Select(e => new MatchRow(e)).ToList();
            s.Rows.AddRange(rows);
            s.NextStart += page.ResultCount;
            s.HasMore = page.ResultCount == 25;
            foreach (var row in rows) _ = FillRowAsync(row);
        }
        catch (Exception ex)
        {
            s.Error = ex.Message;
        }
        finally
        {
            s.Loading = false;
            MatchesChanged?.Invoke();
        }
    }

    private async Task FillRowAsync(MatchRow row)
    {
        await EnterThrottleAsync();
        try
        {
            var info = row.Entry.MatchInfo;
            var map = GetMapAsync(info.MapVariant);
            var mode = GetModeAsync(info.UgcGameVariant);
            var playlist = GetPlaylistAsync(info.Playlist);
            var stats = GetMatchAsync(row.Entry.MatchId);
            row.Map = await map;
            row.Mode = await mode;
            row.Playlist = await playlist;
            var m = await stats;
            var me = m.Players.FirstOrDefault(p => p.Xuid == Me?.Xuid);
            row.Mine = me?.Stats?.CoreStats;
            row.ScoreLine = ScoreLine(m, me);
        }
        catch (Exception) { /* the row just stays without details */ }
        finally
        {
            row.Loaded = true;
            _throttle.Release();
            MatchesChanged?.Invoke();
        }
    }

    /// <summary>"50–43" (your team first) for team games, "3rd" for free-for-all.</summary>
    public static string? ScoreLine(MatchStats m, MatchPlayer? me)
    {
        if (m.MatchInfo.TeamsEnabled && m.Teams.Count >= 2)
        {
            var mine = me?.LastTeamId;
            return string.Join("–", m.Teams.OrderBy(t => t.TeamId == mine ? 0 : 1).ThenBy(t => t.TeamId)
                .Select(t => t.Stats.CoreStats.Score.ToString("N0")));
        }
        return me is null ? null : Fmt.Ordinal(me.Rank);
    }

    // ---- Theater films ------------------------------------------------------------------

    /// <summary>Whether this match's film is already on disk, so its timeline needs no download.</summary>
    public bool IsFilmCached(string matchId) => films.HasFilm(matchId);

    /// <summary>Where films are cached, with the home folder shown as ~.</summary>
    public string FilmFolder => films.DisplayRoot;

    /// <summary>The most the film cache may hold before the oldest films are deleted (setting <c>FilmCacheLimitMb</c>, 2 GB by default).</summary>
    public long FilmLimitBytes => (long.TryParse(config["FilmCacheLimitMb"], out var mb) && mb > 0 ? mb : 2048) << 20;

    /// <summary>Bytes the cached films take up.</summary>
    public long FilmBytes() => films.TotalBytes();

    /// <summary>Deletes every cached film (they download again when a timeline is opened). Returns how many.</summary>
    public int ClearFilms()
    {
        _timelines.Clear();
        return films.Clear();
    }

    /// <summary>
    /// The match's timeline from its Theater film, downloading the film first if it isn't cached.
    /// The manifest needs the token (it goes through the client); chunks are public blobs and are
    /// fetched without it. A failed load isn't kept, so it can be retried.
    /// </summary>
    public Task<MatchTimeline> GetTimelineAsync(string matchId, Action<int, int>? progress = null)
    {
        var task = _timelines.GetOrAdd(matchId, id => LoadTimelineAsync(id, progress));
        _ = task.ContinueWith(t => _timelines.TryRemove(new(matchId, t)), TaskContinuationOptions.NotOnRanToCompletion);
        return task;
    }

    private async Task<MatchTimeline> LoadTimelineAsync(string matchId, Action<int, int>? progress)
    {
        var board = await GetMatchAsync(matchId);
        if (!films.HasFilm(matchId))
        {
            var raw = await Client.GetFilmManifestJsonAsync(matchId);
            var manifest = FilmSource.ParseManifest(raw);
            // Without a summary there's no timeline, so don't download the rest of the film.
            if (!manifest.CustomData.HasSummary) throw new UnfinishedFilmException();
            films.SaveManifest(matchId, raw);
            films.SaveScoreboard(matchId, board);
            var source = new FilmSource(httpFactory.CreateClient());
            var chunks = manifest.CustomData.Chunks;
            var done = 0;
            await Task.WhenAll(chunks.Select(async c =>
            {
                if (!films.HasChunk(matchId, c))
                {
                    await EnterThrottleAsync();
                    try { films.SaveChunk(matchId, c, await source.GetChunkAsync(manifest, c)); }
                    finally { _throttle.Release(); }
                }
                progress?.Invoke(Interlocked.Increment(ref done), chunks.Count);
            }));
            films.Trim(FilmLimitBytes, matchId);
        }
        var navMesh = await GetNavMeshAsync(board.MatchInfo.MapVariant);
        // Decoding reads every chunk (a few seconds for a long match), so keep it off the circuit's thread.
        return await Task.Run(() => MatchTimeline.Build(films, matchId, board, navMesh: navMesh));
    }

    // ---- Shared, immutable data (cached for everyone) ---------------------------------

    /// <summary>A scoreboard: from memory, else the archive, else the API (and then kept in both).</summary>
    public Task<MatchStats> GetMatchAsync(string matchId)
    {
        var client = Client;
        return cache.GetOrAddAsync($"match:{matchId}", async () =>
        {
            if (archive.GetMatch(matchId) is { } stored) return stored;
            var match = await Retry.RunAsync(() => client.GetMatchStatsAsync(matchId));
            archive.PutMatch(match);
            return match;
        });
    }

    public async Task<MatchSkill?> GetSkillAsync(string matchId)
    {
        var xuid = Xuid;
        try
        {
            var client = Client;
            return await cache.GetOrAddAsync($"skill:{matchId}:{xuid}", async () =>
            {
                if (archive.TryGetSkill(xuid, matchId, out var stored)) return stored;   // null: known to have none
                MatchSkill? skill;
                try
                {
                    skill = (await Retry.RunAsync(() => client.GetMatchSkillAsync(matchId, [xuid]))).Value
                        .FirstOrDefault(v => v.ResultCode == 0)?.Result;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { skill = null; }   // no skill data for this match
                // Matches without a result (Firefight, custom games) are remembered so they aren't asked about
                // again, unless the match is recent: it may just not have one yet.
                if (skill is not null || archive.GetMatch(matchId) is { } m && m.MatchInfo.EndTime < DateTimeOffset.UtcNow.AddHours(-2))
                    archive.PutSkill(xuid, matchId, skill);
                return skill;
            });
        }
        catch (HttpRequestException) { return null; }   // e.g. custom games have no skill data
    }

    public Task<UgcAsset?> GetMapAsync(AssetRef? a) => GetUgcAsync("map", a, Client.GetMapAsync);
    public Task<UgcAsset?> GetModeAsync(AssetRef? a) => GetUgcAsync("mode", a, Client.GetGameVariantAsync);

    /// <summary>
    /// A Forge map's navmesh (its walkable floor), or null when the map has none (343-built maps) or
    /// it can't be fetched or read. The blob host is public, so the request carries no token.
    /// </summary>
    public async Task<NavMesh?> GetNavMeshAsync(AssetRef? a)
    {
        if (await GetMapAsync(a) is not { } map || map.BlobFileUri(UgcAsset.NavMeshFile) is not { } uri) return null;
        try
        {
            return await cache.GetOrAddAsync($"navmesh:{map.AssetId}:{map.VersionId}", async () =>
            {
                await EnterThrottleAsync();
                try
                {
                    using var http = httpFactory.CreateClient();
                    http.MaxResponseContentBufferSize = 32 << 20;
                    return NavMesh.Parse(await http.GetByteArrayAsync(uri));
                }
                finally { _throttle.Release(); }
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException) { return null; }
    }

    public async Task<UgcAsset?> GetPlaylistAsync(AssetRef? a)
    {
        var p = await GetUgcAsync("playlist", a, Client.GetPlaylistAsync);
        if (p?.PublicName is { } name) _playlistNames[p.AssetId] = name;
        return p;
    }

    private async Task<UgcAsset?> GetUgcAsync(string kind, AssetRef? a, Func<AssetRef, CancellationToken, Task<UgcAsset>> fetch)
    {
        if (a is null || !HaloIds.IsGuid(a.AssetId) || !HaloIds.IsGuid(a.VersionId) || a.AssetId == Guid.Empty.ToString()) return null;
        try
        {
            return await cache.GetOrAddAsync($"ugc:{kind}:{a.AssetId}:{a.VersionId}", async () =>
            {
                if (archive.GetUgc(kind, a) is { } stored) return stored;
                var asset = await Retry.RunAsync(() => fetch(a, CancellationToken.None));
                archive.PutUgc(kind, asset);
                return asset;
            });
        }
        catch (HttpRequestException) { return null; }   // deleted or private UGC
    }

    public Task<MedalBook> GetMedalBookAsync() =>
        cache.GetOrAddAsync("medalbook", async () =>
        {
            var client = Client;
            var meta = await client.GetMedalMetadataAsync();
            var sprite = meta.Sprites.GetValueOrDefault("medium") ?? meta.Sprites.Values.First();
            var url = await assets.EnsureAsync("waypoint/" + sprite.Path, () => client.GetWaypointFileAsync(sprite.Path));
            return new MedalBook(meta, sprite, url, meta.Medals.DistinctBy(m => m.NameId).ToDictionary(m => m.NameId));
        });

    /// <summary>A URL for a CMS image (e.g. a rank icon), or null if it can't be fetched.</summary>
    public async Task<string?> GetCmsImageUrlAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var client = Client;
        try { return await assets.EnsureAsync("cms/" + path, () => client.GetCmsImageAsync(path)); }
        catch (HttpRequestException) { return null; }
    }

    public async Task<IReadOnlyDictionary<string, UserProfile>> GetUsersAsync(IEnumerable<string> xuids)
    {
        var missing = xuids.Distinct().Where(x => HaloIds.IsXuid(x) && !_users.ContainsKey(x)).ToList();
        foreach (var chunk in missing.Chunk(100))
        {
            try
            {
                foreach (var u in await Client.GetUsersAsync(chunk))
                {
                    _users[u.Xuid] = u;
                    diagnostics.Redactor.Register(u.Gamertag);
                }
            }
            catch (HttpRequestException) { /* fall back to showing no gamertag */ }
        }
        return _users;
    }
}
