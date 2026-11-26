using System.Net;
using System.Text;
using System.Text.Json;

namespace Ancilla.Core;

/// <summary>
/// One endpoint <see cref="HaloWaypointClient"/> calls: the client method that calls it, the model
/// its JSON is read into (null for images), and its fixture, a hand-written response in the real
/// shape with made-up Halo values. The fixtures are in <c>tests/Ancilla.Testing/Fixtures</c>.
/// </summary>
public sealed record ApiEndpoint(
    string Name, string Method, string Host, string Fixture, Type? Model,
    Func<HaloWaypointClient, ApiSample, CancellationToken, Task<object>> Call)
{
    public bool IsImage => Model is null;

    /// <summary>
    /// Big objects Ancilla doesn't read, whose insides the drift check doesn't compare (a test checks
    /// no model reads them). They're still in the fixture, cut down.
    /// </summary>
    public IReadOnlyList<string> Untracked { get; init; } = [];
}

/// <summary>
/// Every endpoint, in the order the drift check calls them. A test checks that every public
/// <c>Get…Async</c> method on <see cref="HaloWaypointClient"/> is here, with a fixture and a
/// parsing test.
/// </summary>
public static class ApiContract
{
    private const string Profile = "profile.svc.halowaypoint.com";
    private const string Settings = "settings.svc.halowaypoint.com";
    private const string Economy = "economy.svc.halowaypoint.com";
    private const string Stats = "halostats.svc.halowaypoint.com";
    private const string GameCms = "gamecms-hacs.svc.halowaypoint.com";
    private const string Skill = "skill.svc.halowaypoint.com";
    private const string Discovery = "discovery-infiniteugc.svc.halowaypoint.com";

    public static IReadOnlyList<ApiEndpoint> Endpoints { get; } =
    [
        new("Who am I", nameof(HaloWaypointClient.GetMeAsync), Profile, "profile-me.json", typeof(UserProfile),
            async (c, s, ct) => await c.GetMeAsync(ct)),
        new("Gamertags", nameof(HaloWaypointClient.GetUsersAsync), Profile, "profile-users.json", typeof(List<UserProfile>),
            async (c, s, ct) => await c.GetUsersAsync([s.Xuid], ct)),
        new("Clearance (flight)", nameof(HaloWaypointClient.GetClearanceAsync), Settings, "flight.json", typeof(FlightConfiguration),
            async (c, s, ct) => await c.GetClearanceAsync(s.Xuid, ct)),
        new("Career rank", nameof(HaloWaypointClient.GetCareerRankAsync), Economy, "career-rank.json", typeof(RewardTrackProgress),
            async (c, s, ct) => await c.GetCareerRankAsync(s.Xuid, ct)),
        new("Operations", nameof(HaloWaypointClient.GetOperationsAsync), Economy, "operations.json", typeof(OperationsResponse),
            async (c, s, ct) => await c.GetOperationsAsync(s.Xuid, ct)),
        new("Service tag", nameof(HaloWaypointClient.GetAppearanceAsync), Economy, "appearance.json", typeof(AppearanceResponse),
            async (c, s, ct) => await c.GetAppearanceAsync(s.Xuid, ct)),
        new("Reward track definition", nameof(HaloWaypointClient.GetRewardTrackDefinitionAsync), GameCms, "reward-track.json",
            typeof(RewardTrackDefinition), async (c, s, ct) => await c.GetRewardTrackDefinitionAsync(s.RewardTrackPath, ct)),
        new("Medal metadata", nameof(HaloWaypointClient.GetMedalMetadataAsync), GameCms, "medal-metadata.json", typeof(MedalMetadata),
            async (c, s, ct) => await c.GetMedalMetadataAsync(ct)),
        new("Service record", nameof(HaloWaypointClient.GetServiceRecordAsync), Stats, "service-record.json", typeof(ServiceRecord),
            async (c, s, ct) => await c.GetServiceRecordAsync(s.Xuid, ct)),
        new("Match history", nameof(HaloWaypointClient.GetMatchHistoryAsync), Stats, "match-history.json", typeof(MatchHistory),
            async (c, s, ct) => await c.GetMatchHistoryAsync(s.Xuid, 0, 25, MatchHistoryType.All, ct)),
        new("Match stats", nameof(HaloWaypointClient.GetMatchStatsAsync), Stats, "match-stats.json", typeof(MatchStats),
            async (c, s, ct) => await c.GetMatchStatsAsync(s.MatchId, ct)),
        new("Match skill", nameof(HaloWaypointClient.GetMatchSkillAsync), Skill, "match-skill.json", typeof(SkillResponse<MatchSkill>),
            async (c, s, ct) => await c.GetMatchSkillAsync(s.SkillMatchId, [s.Xuid], ct))
        {
            Untracked = ["Value[].Result.Counterfactuals.TierCounterfactuals"],   // one entry per tier, when there are any
        },
        new("Playlist CSR", nameof(HaloWaypointClient.GetPlaylistCsrAsync), Skill, "playlist-csr.json", typeof(SkillResponse<PlaylistCsr>),
            async (c, s, ct) => await c.GetPlaylistCsrAsync(s.CsrPlaylistId, s.Xuid, ct)),
        new("Map", nameof(HaloWaypointClient.GetMapAsync), Discovery, "ugc-map.json", typeof(UgcAsset),
            async (c, s, ct) => await c.GetMapAsync(s.Map, ct)),
        new("Game variant", nameof(HaloWaypointClient.GetGameVariantAsync), Discovery, "ugc-game-variant.json", typeof(UgcAsset),
            async (c, s, ct) => await c.GetGameVariantAsync(s.GameVariant, ct))
        {
            // Every setting of the mode, and the engine variant it's built on.
            Untracked = ["CustomData.KeyValues", "EngineGameVariantLink"],
        },
        new("Playlist", nameof(HaloWaypointClient.GetPlaylistAsync), Discovery, "ugc-playlist.json", typeof(UgcAsset),
            async (c, s, ct) => await c.GetPlaylistAsync(s.Playlist, ct))
        {
            Untracked = ["RotationEntries"],   // every map and mode in the rotation
        },
        new("Film manifest", nameof(HaloWaypointClient.GetFilmManifestJsonAsync), Discovery, "film-spectate.json", typeof(FilmManifest),
            async (c, s, ct) => await c.GetFilmManifestJsonAsync(s.MatchId, ct)),
        new("CMS image", nameof(HaloWaypointClient.GetCmsImageAsync), GameCms, "image.png", null,
            async (c, s, ct) => await c.GetCmsImageAsync(s.CmsImagePath, ct)),
        new("Waypoint file", nameof(HaloWaypointClient.GetWaypointFileAsync), GameCms, "image.png", null,
            async (c, s, ct) => await c.GetWaypointFileAsync(s.WaypointFilePath, ct)),
    ];
}

/// <summary>Thrown by <see cref="ApiSample"/> when the player's data has nothing to call an endpoint with.</summary>
public sealed class SampleUnavailableException(string reason) : Exception(reason);

/// <summary>
/// The IDs the drift check calls each endpoint with, found in the signed-in player's own data:
/// their latest matchmade match that isn't Firefight (which has no skill results) and its map, mode
/// and playlist, their career track and a medal sheet.
/// </summary>
public sealed class ApiSample
{
    /// <summary>Ranked Arena. CSR is asked for here even if the player has never played ranked.</summary>
    public const string RankedArena = "edfef3ac-9cbe-4fa2-b949-8f29deafd483";

    public required string Xuid { get; init; }
    /// <summary>The player's gamertag, so the report can be checked for it. Never sent anywhere.</summary>
    public string? Gamertag { get; init; }
    public string? LatestMatchId { get; init; }
    /// <summary>The latest match is Firefight: every one of the last 25 matchmade matches was.</summary>
    public bool LatestMatchIsFirefight { get; init; }
    public AssetRef? LatestMap { get; init; }
    public AssetRef? LatestGameVariant { get; init; }
    public AssetRef? LatestPlaylist { get; init; }
    public string CsrPlaylistId { get; init; } = RankedArena;
    public string? CareerTrackPath { get; init; }
    public string? RankIconPath { get; init; }
    public string? MedalSheetPath { get; init; }

    public string MatchId => LatestMatchId ?? throw new SampleUnavailableException("no matchmade match in your history");
    public string SkillMatchId => LatestMatchIsFirefight
        ? throw new SampleUnavailableException("your last 25 matchmade matches were all Firefight, which has no skill results")
        : MatchId;
    public AssetRef Map => LatestMap ?? throw new SampleUnavailableException("your latest match has no map");
    public AssetRef GameVariant => LatestGameVariant ?? throw new SampleUnavailableException("your latest match has no game variant");
    public AssetRef Playlist => LatestPlaylist ?? throw new SampleUnavailableException("your latest match has no playlist");
    public string RewardTrackPath => CareerTrackPath ?? throw new SampleUnavailableException("career rank didn't load");
    public string CmsImagePath => RankIconPath ?? throw new SampleUnavailableException("the career track has no rank icon");
    public string WaypointFilePath => MedalSheetPath ?? throw new SampleUnavailableException("medal metadata has no sprite sheet");

    /// <summary>Finds the IDs in the player's data. Only "who am I" has to work; anything else missing skips what needs it.</summary>
    public static async Task<ApiSample> DiscoverAsync(HaloWaypointClient client, CancellationToken ct = default)
    {
        var me = await client.GetMeAsync(ct);
        var xuid = HaloIds.RequireXuid(me.Xuid);

        MatchHistoryEntry? latest = null;
        try
        {
            var history = await client.GetMatchHistoryAsync(xuid, 0, 25, MatchHistoryType.Matchmaking, ct);
            var matches = history.Results.Where(r => HaloIds.IsGuid(r.MatchId)).ToList();
            latest = matches.FirstOrDefault(r => r.MatchInfo.GameVariantCategory != GameVariantCategories.Firefight)
                ?? matches.FirstOrDefault();
        }
        catch (HttpRequestException) { }

        string? track = null, icon = null, sheet = null;
        try
        {
            track = (await client.GetCareerRankAsync(xuid, ct)).RewardTrackPath;
            icon = (await client.GetRewardTrackDefinitionAsync(track, ct)).Ranks.Select(r => r.RankIcon).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        }
        catch (HttpRequestException) { }
        try { sheet = (await client.GetMedalMetadataAsync(ct)).Sprites.Values.FirstOrDefault()?.Path; }
        catch (HttpRequestException) { }

        static AssetRef? Valid(AssetRef? a) => a is not null && HaloIds.IsGuid(a.AssetId) && HaloIds.IsGuid(a.VersionId) ? a : null;
        return new ApiSample
        {
            Xuid = xuid,
            Gamertag = me.Gamertag,
            LatestMatchId = latest?.MatchId,
            LatestMatchIsFirefight = latest?.MatchInfo.GameVariantCategory == GameVariantCategories.Firefight,
            LatestMap = Valid(latest?.MatchInfo.MapVariant),
            LatestGameVariant = Valid(latest?.MatchInfo.UgcGameVariant),
            LatestPlaylist = Valid(latest?.MatchInfo.Playlist),
            CareerTrackPath = track,
            RankIconPath = icon,
            MedalSheetPath = sheet,
        };
    }
}

/// <summary>
/// Keeps a copy of every JSON response that passes through it, in memory only, so the drift check
/// can look at the body the client just read. Put it in front of the handler that sends requests.
/// </summary>
public sealed class ResponseRecorder : DelegatingHandler
{
    public sealed record Recorded(Uri Uri, HttpStatusCode Status, string? MediaType, string? Json);

    private readonly List<Recorded> _responses = [];

    public IReadOnlyList<Recorded> Responses
    {
        get { lock (_responses) return [.. _responses]; }
    }

    public void Clear()
    {
        lock (_responses) _responses.Clear();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var res = await base.SendAsync(request, ct);
        var type = res.Content.Headers.ContentType?.MediaType;
        string? json = null;
        if (res.IsSuccessStatusCode && type?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            await res.Content.LoadIntoBufferAsync(ct);
            json = await res.Content.ReadAsStringAsync(ct);
        }
        lock (_responses) _responses.Add(new(request.RequestUri!, res.StatusCode, type, json));
        return res;
    }
}

/// <summary>What the drift check found for one endpoint.</summary>
public sealed record EndpointDrift(ApiEndpoint Endpoint, IReadOnlyList<DriftItem> Items, string? Failure = null, string? Skipped = null)
{
    /// <summary>The endpoint failed, or a change could break Ancilla.</summary>
    public bool Breaks => Failure is not null || Items.Any(i => i.Breaks);
}

public sealed record DriftReport(IReadOnlyList<EndpointDrift> Endpoints)
{
    public bool Breaks => Endpoints.Any(e => e.Breaks);

    /// <summary>
    /// The report as text: endpoint names, field paths and kinds, never a value. With
    /// <paramref name="detail"/>, every field inside an added object is listed too, for updating fixtures.
    /// </summary>
    public string Format(bool detail = false)
    {
        var sb = new StringBuilder();
        var checkedCount = Endpoints.Count(e => e.Skipped is null);
        sb.AppendLine($"Halo API drift check: {Endpoints.Count} endpoints, {checkedCount} checked.");
        sb.AppendLine();
        var width = Endpoints.Max(e => e.Endpoint.Name.Length) + 2;
        foreach (var e in Endpoints)
        {
            var status = e.Skipped is { } skip ? $"skipped: {skip}"
                : e.Failure is { } fail ? $"FAILED: {fail}"
                : e.Items.Count == 0 ? "no change"
                : $"{e.Items.Count} change{(e.Items.Count == 1 ? "" : "s")}" + (e.Breaks ? ", could break Ancilla" : "");
            sb.AppendLine($"{e.Endpoint.Name.PadRight(width)}{status}");
            foreach (var group in e.Items.GroupBy(i => i.Breaks).OrderByDescending(g => g.Key))
            {
                sb.AppendLine(group.Key ? "  Could break Ancilla:" : "  Doesn't break Ancilla (update the fixture to match):");
                foreach (var i in group)
                {
                    sb.AppendLine($"    {i.Kind.ToString().ToLowerInvariant(),-8} {(i.Path.Length == 0 ? "(the whole response)" : i.Path)}: {i.Detail}");
                    if (detail)
                        foreach (var field in i.Inside ?? [])
                            sb.AppendLine($"               {field}");
                }
            }
        }
        sb.AppendLine();
        sb.AppendLine(Breaks
            ? "Something Ancilla needs has changed or failed. Fix the models, then update the fixtures to the new shape."
            : Endpoints.Any(e => e.Items.Count > 0)
                ? "Nothing Ancilla needs has changed. Update the fixtures to the new shape when convenient."
                : "Every response has the shape its fixture has.");
        return sb.ToString();
    }
}

/// <summary>
/// The drift check: calls every endpoint with the player's own IDs, and compares each response's
/// shape with what the models read and with its fixture. Responses stay in memory, and the report
/// holds field names only.
/// </summary>
public static class ApiDrift
{
    /// <param name="client">
    /// A client that hasn't made an economy call yet, so the clearance endpoint is really called
    /// (the client keeps the clearance it got). <paramref name="recorder"/> must be in its handler chain.
    /// </param>
    /// <param name="fixture">An endpoint's fixture by file name, or null if there isn't one.</param>
    public static async Task<DriftReport> RunAsync(HaloWaypointClient client, ResponseRecorder recorder, ApiSample sample,
        Func<string, string?> fixture, Redactor redactor, CancellationToken ct = default)
    {
        var results = new List<EndpointDrift>();
        foreach (var endpoint in ApiContract.Endpoints)
            results.Add(await CheckAsync(endpoint, client, recorder, sample, fixture, redactor, ct));
        return new DriftReport(results);
    }

    private static async Task<EndpointDrift> CheckAsync(ApiEndpoint endpoint, HaloWaypointClient client, ResponseRecorder recorder,
        ApiSample sample, Func<string, string?> fixture, Redactor redactor, CancellationToken ct)
    {
        recorder.Clear();
        string? failure = null;
        object? result = null;
        try { result = await endpoint.Call(client, sample, ct); }
        catch (SampleUnavailableException ex) { return new(endpoint, [], Skipped: ex.Message); }
        catch (HttpRequestException ex) { failure = redactor.Redact(ex.Message); }
        catch (JsonException ex) { failure = "doesn't parse: " + redactor.Redact(ex.Message); }

        var response = recorder.Responses.LastOrDefault(r => r.Uri.Host == endpoint.Host);
        if (endpoint.IsImage)
            return failure is not null || result is not (byte[], string type) || !type.StartsWith("image/", StringComparison.Ordinal)
                ? new(endpoint, [], failure ?? $"not an image ({redactor.Redact(response?.MediaType ?? "no type")})")
                : new(endpoint, []);
        if (response?.Json is not { } json)
            return new(endpoint, [], failure ?? (response is { Status: var status } && (int)status >= 400
                ? $"{(int)status} {status}" : "no JSON in the response"));

        var model = ModelShape.Of(endpoint.Model!);
        JsonShape live;
        try { live = JsonShape.Parse(json, model.Dictionaries); }
        catch (JsonException) { return new(endpoint, [], "the response isn't JSON"); }
        var known = fixture(endpoint.Fixture) is { } text ? JsonShape.Parse(text, model.Dictionaries) : null;

        // Field paths are names from Halo's own schema, but run them through the redactor anyway.
        var items = ShapeDiff.Compare(model, known, live, endpoint.Untracked)
            .Select(i => i with { Path = redactor.Redact(i.Path), Inside = i.Inside?.Select(redactor.Redact).ToList() }).ToList();
        return new(endpoint, items, failure);
    }
}
