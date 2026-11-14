using System.Collections.Concurrent;
using Ancilla.Core;

namespace Ancilla.Web.Services;

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
    public bool IsPve(string xuid) =>
        Stats.Players.FirstOrDefault(p => p.Xuid == xuid)?.Stats?.ModeStats is { } m
        && m.ContainsKey("PveStats") && !m.ContainsKey("PvpStats");
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
public sealed class HaloSession(IHttpClientFactory httpFactory, HaloCache cache, AssetCache assets, IConfiguration config)
{
    // The ranked-playlist list isn't discoverable by name without a version ID, so seed the common one.
    private static readonly Dictionary<string, string> KnownPlaylists = new()
    {
        ["edfef3ac-9cbe-4fa2-b949-8f29deafd483"] = "Ranked Arena",
    };

    private readonly ConcurrentDictionary<string, UserProfile> _users = new();
    private readonly ConcurrentDictionary<string, string> _playlistNames = new(KnownPlaylists);
    private readonly SemaphoreSlim _throttle = new(8);
    private readonly SemaphoreSlim _historyLock = new(1);
    private readonly Dictionary<MatchHistoryType, List<MatchHistoryEntry>> _history = [];
    private readonly HashSet<MatchHistoryType> _historyComplete = [];
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

    /// <summary>Raised on sign-in/out.</summary>
    public event Action? Changed;
    /// <summary>Raised (possibly off the UI thread) whenever match rows load or fill in.</summary>
    public event Action? MatchesChanged;

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
        var client = new HaloWaypointClient(httpFactory.CreateClient(), token);
        var me = await client.GetMeAsync();   // validates the token
        Reset();
        _client = client;
        Me = me;
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
        _history.Clear();
        _historyComplete.Clear();
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
            await _throttle.WaitAsync();
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
            await _throttle.WaitAsync();
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
    /// The most recent <paramref name="count"/> matches with their scoreboards, newest first.
    /// History pages are kept for the session (call <see cref="ForgetHistory"/> to pick up new
    /// games); scoreboards come from the shared cache. <paramref name="progress"/> gets (done, total).
    /// </summary>
    public async Task<IReadOnlyList<RecentMatch>> GetRecentMatchesAsync(MatchHistoryType type, int count, Action<int, int>? progress = null)
    {
        var entries = await GetHistoryAsync(type, count);
        var done = 0;
        var results = await Task.WhenAll(entries.Select(async e =>
        {
            await _throttle.WaitAsync();
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
            await _throttle.WaitAsync();
            try { return (id, skill: await GetSkillAsync(id)); }
            finally { _throttle.Release(); }
        }));
        return pairs.Where(p => p.skill is not null).ToDictionary(p => p.id, p => p.skill!);
    }

    public void ForgetHistory()
    {
        _history.Clear();
        _historyComplete.Clear();
    }

    private async Task<List<MatchHistoryEntry>> GetHistoryAsync(MatchHistoryType type, int count)
    {
        await _historyLock.WaitAsync();   // two pages loading at once mustn't interleave pages
        try
        {
            if (!_history.TryGetValue(type, out var list)) _history[type] = list = [];
            while (list.Count < count && !_historyComplete.Contains(type))
            {
                var page = await Client.GetMatchHistoryAsync(Xuid, list.Count, 25, type);
                list.AddRange(page.Results);
                if (page.ResultCount < 25) _historyComplete.Add(type);
            }
            return list.Take(count).ToList();
        }
        finally
        {
            _historyLock.Release();
        }
    }

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
        await _throttle.WaitAsync();
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

    // ---- Shared, immutable data (cached for everyone) ---------------------------------

    public Task<MatchStats> GetMatchAsync(string matchId) =>
        cache.GetOrAddAsync($"match:{matchId}", () => Client.GetMatchStatsAsync(matchId));

    public async Task<MatchSkill?> GetSkillAsync(string matchId)
    {
        var xuid = Xuid;
        try
        {
            return await cache.GetOrAddAsync($"skill:{matchId}:{xuid}", async () =>
                (await Client.GetMatchSkillAsync(matchId, [xuid])).Value.FirstOrDefault(v => v.ResultCode == 0)?.Result);
        }
        catch (HttpRequestException) { return null; }   // e.g. custom games have no skill data
    }

    public Task<UgcAsset?> GetMapAsync(AssetRef? a) => GetUgcAsync("map", a, Client.GetMapAsync);
    public Task<UgcAsset?> GetModeAsync(AssetRef? a) => GetUgcAsync("mode", a, Client.GetGameVariantAsync);

    public async Task<UgcAsset?> GetPlaylistAsync(AssetRef? a)
    {
        var p = await GetUgcAsync("playlist", a, Client.GetPlaylistAsync);
        if (p?.PublicName is { } name) _playlistNames[p.AssetId] = name;
        return p;
    }

    private async Task<UgcAsset?> GetUgcAsync(string kind, AssetRef? a, Func<AssetRef, CancellationToken, Task<UgcAsset>> fetch)
    {
        if (a is null || !HaloIds.IsGuid(a.AssetId) || !HaloIds.IsGuid(a.VersionId) || a.AssetId == Guid.Empty.ToString()) return null;
        try { return await cache.GetOrAddAsync($"ugc:{kind}:{a.AssetId}:{a.VersionId}", () => fetch(a, CancellationToken.None)); }
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
                foreach (var u in await Client.GetUsersAsync(chunk)) _users[u.Xuid] = u;
            }
            catch (HttpRequestException) { /* fall back to showing no gamertag */ }
        }
        return _users;
    }
}
