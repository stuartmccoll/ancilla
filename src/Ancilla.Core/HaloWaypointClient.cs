using System.Net.Http.Json;
using System.Text.Json;

namespace Ancilla.Core;

/// <summary>
/// Minimal client for the Halo Infinite services that back halowaypoint.com.
///
/// Auth: every call sends the Spartan token in the <c>x-343-authorization-spartan</c> header
/// (the value of Waypoint's <c>343-spartan-token</c> cookie, including its "v4=" prefix).
/// Economy calls additionally need a <c>343-clearance</c> header (the active flight ID),
/// which this client resolves automatically.
/// </summary>
public sealed class HaloWaypointClient
{
    private const string Profile = "https://profile.svc.halowaypoint.com";
    private const string Settings = "https://settings.svc.halowaypoint.com";
    private const string Economy = "https://economy.svc.halowaypoint.com";
    private const string Stats = "https://halostats.svc.halowaypoint.com";
    private const string GameCms = "https://gamecms-hacs.svc.halowaypoint.com";
    private const string Skill = "https://skill.svc.halowaypoint.com";
    private const string Discovery = "https://discovery-infiniteugc.svc.halowaypoint.com";

    // Any recent-looking build string works; the service only uses it to pick a flight.
    private const string FlightBuild = "222249.22.06.08.1730-0";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _spartanToken;
    private string? _clearance;

    public HaloWaypointClient(HttpClient http, string spartanToken)
    {
        _http = http;
        _spartanToken = NormaliseToken(spartanToken);
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ancilla/1.0");
    }

    /// <summary>Accepts the cookie value raw or URL-encoded, with or without "v4=".</summary>
    public static string NormaliseToken(string token)
    {
        var t = Uri.UnescapeDataString(token.Trim().Trim('"'));
        return t.StartsWith("v4=", StringComparison.Ordinal) ? t : "v4=" + t;
    }

    public Task<UserProfile> GetMeAsync(CancellationToken ct = default) =>
        GetAsync<UserProfile>($"{Profile}/users/me", needsClearance: false, ct);

    public async Task<string> GetClearanceAsync(string xuid, CancellationToken ct = default)
    {
        if (_clearance is not null) return _clearance;
        HaloIds.RequireXuid(xuid);

        try
        {
            var flight = await GetAsync<FlightConfiguration>(
                $"{Settings}/oban/flight-configurations/titles/hi/audiences/RETAIL/players/xuid({xuid})/active?sandbox=UNUSED&build={FlightBuild}",
                needsClearance: false, ct);
            _clearance = flight.FlightConfigurationId;
        }
        catch (HttpRequestException)
        {
            // Fallback: every match records the clearance it was played under.
            var history = await GetMatchHistoryAsync(xuid, 0, 1, MatchHistoryType.All, ct);
            _clearance = history.Results.FirstOrDefault()?.MatchInfo.ClearanceId
                ?? throw new InvalidOperationException("Could not determine a clearance (flight) ID.");
        }
        return _clearance;
    }

    public Task<RewardTrackProgress> GetCareerRankAsync(string xuid, CancellationToken ct = default) =>
        GetAsync<RewardTrackProgress>(
            $"{Economy}/hi/players/xuid({HaloIds.RequireXuid(xuid)})/rewardtracks/careerranks/careerrank1", needsClearance: true, ct, xuid);

    public Task<OperationsResponse> GetOperationsAsync(string xuid, CancellationToken ct = default) =>
        GetAsync<OperationsResponse>(
            $"{Economy}/hi/players/xuid({HaloIds.RequireXuid(xuid)})/rewardtracks/operations", needsClearance: true, ct, xuid);

    /// <param name="rewardTrackPath">e.g. "RewardTracks/Operations/S13Op01.json"</param>
    public Task<RewardTrackDefinition> GetRewardTrackDefinitionAsync(string rewardTrackPath, CancellationToken ct = default) =>
        GetAsync<RewardTrackDefinition>($"{GameCms}/hi/Progression/file/{rewardTrackPath}", needsClearance: false, ct);

    public Task<ServiceRecord> GetServiceRecordAsync(string xuid, CancellationToken ct = default) =>
        GetServiceRecordAsync(xuid, null, null, ct);

    /// <summary>
    /// The matchmade service record, optionally narrowed to one game mode category
    /// (<see cref="GameVariantCategories"/>) and/or ranked or unranked matches. Both filters can be
    /// combined. Only one category can be given: repeating the parameter gives meaningless totals.
    /// </summary>
    public Task<ServiceRecord> GetServiceRecordAsync(string xuid, int? gameVariantCategory, bool? isRanked, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (gameVariantCategory is { } c) query.Add($"gameVariantCategory={c}");
        if (isRanked is { } r) query.Add($"isRanked={(r ? "true" : "false")}");
        var qs = query.Count == 0 ? "" : "?" + string.Join('&', query);
        return GetAsync<ServiceRecord>($"{Stats}/hi/players/xuid({HaloIds.RequireXuid(xuid)})/Matchmade/servicerecord{qs}", needsClearance: false, ct);
    }

    /// <param name="count">The service caps this at 25.</param>
    public Task<MatchHistory> GetMatchHistoryAsync(string xuid, int start = 0, int count = 25,
        MatchHistoryType type = MatchHistoryType.All, CancellationToken ct = default) =>
        GetAsync<MatchHistory>($"{Stats}/hi/players/xuid({HaloIds.RequireXuid(xuid)})/matches?start={start}&count={Math.Clamp(count, 1, 25)}&type={type}",
            needsClearance: false, ct);

    /// <summary>Full scoreboard for one match: every player and team, with medals and mode stats.</summary>
    public Task<MatchStats> GetMatchStatsAsync(string matchId, CancellationToken ct = default) =>
        GetAsync<MatchStats>($"{Stats}/hi/matches/{HaloIds.RequireGuid(matchId, nameof(matchId))}/stats", needsClearance: false, ct);

    /// <summary>MMR, CSR change and expected-vs-actual kills/deaths for the given players in a match.</summary>
    public Task<SkillResponse<MatchSkill>> GetMatchSkillAsync(string matchId, IEnumerable<string> xuids, CancellationToken ct = default) =>
        GetAsync<SkillResponse<MatchSkill>>(
            $"{Skill}/hi/matches/{HaloIds.RequireGuid(matchId, nameof(matchId))}/skill?{string.Join('&', xuids.Select(x => $"players=xuid({HaloIds.RequireXuid(x)})"))}", needsClearance: false, ct);

    /// <summary>Current, season-best and all-time-best CSR. Fails (400/404) for unranked playlists.</summary>
    public Task<SkillResponse<PlaylistCsr>> GetPlaylistCsrAsync(string playlistId, string xuid, CancellationToken ct = default) =>
        GetAsync<SkillResponse<PlaylistCsr>>($"{Skill}/hi/playlist/{HaloIds.RequireGuid(playlistId, nameof(playlistId))}/csrs?players=xuid({HaloIds.RequireXuid(xuid)})", needsClearance: false, ct);

    public Task<UgcAsset> GetMapAsync(AssetRef a, CancellationToken ct = default) => GetUgcAsync("maps", a, ct);
    public Task<UgcAsset> GetGameVariantAsync(AssetRef a, CancellationToken ct = default) => GetUgcAsync("ugcGameVariants", a, ct);
    public Task<UgcAsset> GetPlaylistAsync(AssetRef a, CancellationToken ct = default) => GetUgcAsync("playlists", a, ct);

    private Task<UgcAsset> GetUgcAsync(string kind, AssetRef a, CancellationToken ct) =>
        GetAsync<UgcAsset>($"{Discovery}/hi/{kind}/{HaloIds.RequireGuid(a.AssetId, "asset ID")}/versions/{HaloIds.RequireGuid(a.VersionId, "version ID")}", needsClearance: false, ct);

    /// <summary>
    /// The Theater film manifest for a match, as raw JSON: chunk list, sizes and the blob storage
    /// prefix. Chunks themselves are public blobs and need no token, so they aren't fetched here.
    /// </summary>
    public async Task<string> GetFilmManifestJsonAsync(string matchId, CancellationToken ct = default)
    {
        using var res = await SendAsync($"{Discovery}/hi/films/matches/{HaloIds.RequireGuid(matchId, nameof(matchId))}/spectate",
            needsClearance: false, ct, accept: "application/json");
        return await res.Content.ReadAsStringAsync(ct);
    }

    public Task<MedalMetadata> GetMedalMetadataAsync(CancellationToken ct = default) =>
        GetAsync<MedalMetadata>($"{GameCms}/hi/Waypoint/file/medals/metadata.json", needsClearance: false, ct);

    /// <summary>Gamertags and gamerpics for up to 100 players at once.</summary>
    public Task<List<UserProfile>> GetUsersAsync(IEnumerable<string> xuids, CancellationToken ct = default) =>
        GetAsync<List<UserProfile>>($"{Profile}/users?xuids={string.Join(',', xuids.Select(HaloIds.RequireXuid))}", needsClearance: false, ct);

    public Task<AppearanceResponse> GetAppearanceAsync(string xuid, CancellationToken ct = default) =>
        GetAsync<AppearanceResponse>($"{Economy}/hi/players/xuid({HaloIds.RequireXuid(xuid)})/customization/appearance", needsClearance: true, ct, xuid);

    /// <summary>A CMS image such as a career rank icon ("career_rank/ProgressWidget/...png"). Needs auth.</summary>
    public Task<(byte[] Bytes, string ContentType)> GetCmsImageAsync(string path, CancellationToken ct = default) =>
        GetBytesAsync($"{GameCms}/hi/images/file/{path}", ct);

    /// <summary>A Waypoint CMS file such as a medal sprite sheet ("medals/images/medal_sheet_med.png"). Needs auth.</summary>
    public Task<(byte[] Bytes, string ContentType)> GetWaypointFileAsync(string path, CancellationToken ct = default) =>
        GetBytesAsync($"{GameCms}/hi/Waypoint/file/{path}", ct);

    private async Task<(byte[] Bytes, string ContentType)> GetBytesAsync(string url, CancellationToken ct)
    {
        using var res = await SendAsync(url, needsClearance: false, ct);
        var type = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return (await res.Content.ReadAsByteArrayAsync(ct), type);
    }

    private async Task<T> GetAsync<T>(string url, bool needsClearance, CancellationToken ct, string? xuid = null)
    {
        using var res = await SendAsync(url, needsClearance, ct, xuid, "application/json");
        return await res.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new InvalidOperationException($"Empty response from {url}");
    }

    private async Task<HttpResponseMessage> SendAsync(string url, bool needsClearance, CancellationToken ct,
        string? xuid = null, string? accept = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("x-343-authorization-spartan", _spartanToken);
        if (accept is not null) req.Headers.Accept.ParseAdd(accept);
        if (needsClearance)
            req.Headers.Add("343-clearance", await GetClearanceAsync(xuid!, ct));

        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            using var _ = res;
            var detail = res.Headers.TryGetValues("x-343-exception", out var ex)
                ? Uri.UnescapeDataString(ex.First()).Split('\n')[0]
                : "";
            var hint = res.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? " (Spartan token missing or expired - grab a fresh one from halowaypoint.com)"
                : "";
            throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase} for {url}{hint} {detail}".Trim(),
                null, res.StatusCode);
        }
        return res;
    }
}
