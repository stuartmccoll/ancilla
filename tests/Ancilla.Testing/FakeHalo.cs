using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ancilla.Core;

namespace Ancilla.Testing;

/// <summary>
/// A fake of the Halo Waypoint services, answering the same URLs <see cref="HaloWaypointClient"/>
/// calls with data from a <see cref="HaloWorld"/>. JSON uses the services' PascalCase names.
/// Requests without <see cref="Spartans.Token"/> get a 401, as they would for real. Nothing leaves
/// the process.
/// </summary>
public sealed partial class FakeHalo
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };

    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly ConcurrentQueue<(Func<Uri, bool> Match, HttpStatusCode Status)> _failures = new();

    public FakeHalo(HaloWorld? world = null) => World = world ?? new HaloWorld();

    public HaloWorld World { get; }

    private int _inFlight, _maxInFlight;

    /// <summary>How long each answer takes. Zero by default.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>The most requests that were ever being answered at once.</summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Every request received, in order.</summary>
    public IReadOnlyCollection<RecordedRequest> Requests => _requests;

    /// <summary>Makes every request whose URL matches fail with <paramref name="status"/>.</summary>
    public FakeHalo Fail(Func<Uri, bool> match, HttpStatusCode status = HttpStatusCode.InternalServerError)
    {
        _failures.Enqueue((match, status));
        return this;
    }

    /// <summary>Makes requests whose URL contains <paramref name="fragment"/> fail.</summary>
    public FakeHalo Fail(string fragment, HttpStatusCode status = HttpStatusCode.InternalServerError) =>
        Fail(u => u.ToString().Contains(fragment, StringComparison.OrdinalIgnoreCase), status);

    /// <summary>A new handler on this fake. Handlers are cheap: the HTTP client factory may dispose them.</summary>
    public HttpMessageHandler CreateHandler() => new Handler(this);

    public HttpClient CreateClient() => new(CreateHandler());

    public IHttpClientFactory Factory => new ClientFactory(this);

    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers);

    private sealed class ClientFactory(FakeHalo fake) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => fake.CreateClient();
    }

    private sealed class Handler(FakeHalo fake) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref fake._inFlight);
            try
            {
                int seen;
                while (now > (seen = Volatile.Read(ref fake._maxInFlight))
                       && Interlocked.CompareExchange(ref fake._maxInFlight, now, seen) != seen) { }
                if (fake.Latency > TimeSpan.Zero) await Task.Delay(fake.Latency, ct);
                else await Task.Yield();   // answer asynchronously, as a real server would
                ct.ThrowIfCancellationRequested();
                return fake.Answer(request);
            }
            finally
            {
                Interlocked.Decrement(ref fake._inFlight);
            }
        }
    }

    // ---- Routing ----------------------------------------------------------------------

    private HttpResponseMessage Answer(HttpRequestMessage req)
    {
        var uri = req.RequestUri ?? throw new InvalidOperationException("No request URI.");
        var headers = req.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        _requests.Enqueue(new RecordedRequest(req.Method, uri, headers));

        foreach (var (match, status) in _failures)
            if (match(uri)) return Status(status);

        // Public blob hosts (film chunks, navmeshes) need no token. None exist in this world.
        if (uri.Host.StartsWith("blobs-", StringComparison.Ordinal)) return Status(HttpStatusCode.NotFound);

        if (!headers.TryGetValue("x-343-authorization-spartan", out var token) || token != Spartans.Token)
        {
            var res = Status(HttpStatusCode.Unauthorized);
            res.Headers.Add("x-343-exception", Uri.EscapeDataString("Spartan token is not valid.\nsecond line"));
            return res;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var q = ParseQuery(uri.Query);
        var w = World;
        return uri.Host switch
        {
            "profile.svc.halowaypoint.com" when path == "/users/me" => Ok(w.Me),
            "profile.svc.halowaypoint.com" when path == "/users" =>
                Ok(q["xuids"].Split(',').Select(x => Spartans.All.FirstOrDefault(s => s.Xuid == x))
                    .OfType<Spartans.Spartan>().Select(HaloWorld.Profile).ToList()),

            "settings.svc.halowaypoint.com" when FlightPath().IsMatch(path) => Ok(new FlightConfiguration(HaloWorld.Ids.Flight)),

            "economy.svc.halowaypoint.com" when !headers.ContainsKey("343-clearance") => Status(HttpStatusCode.Forbidden),
            "economy.svc.halowaypoint.com" when path.EndsWith("/rewardtracks/careerranks/careerrank1") => Ok(HaloWorld.CareerProgress()),
            "economy.svc.halowaypoint.com" when path.EndsWith("/rewardtracks/operations") => Ok(HaloWorld.Operations()),
            "economy.svc.halowaypoint.com" when path.EndsWith("/customization/appearance") =>
                Ok(new AppearanceResponse(new Appearance(Spartans.NobleSix.ServiceTag))),

            "gamecms-hacs.svc.halowaypoint.com" when path.StartsWith("/hi/Progression/file/") =>
                HaloWorld.Definition(path["/hi/Progression/file/".Length..]) is { } def ? Ok(def) : Status(HttpStatusCode.NotFound),
            "gamecms-hacs.svc.halowaypoint.com" when path == "/hi/Waypoint/file/medals/metadata.json" => Ok(HaloWorld.Medals.Metadata()),
            "gamecms-hacs.svc.halowaypoint.com" when path.StartsWith("/hi/Waypoint/file/") || path.StartsWith("/hi/images/file/") => Image(),

            "halostats.svc.halowaypoint.com" => Stats(path, q),
            "skill.svc.halowaypoint.com" => Skill(path),
            "discovery-infiniteugc.svc.halowaypoint.com" => Discovery(path),
            _ => Status(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage Stats(string path, Dictionary<string, string> q)
    {
        if (ServiceRecordPath().Match(path) is { Success: true } sr)
        {
            if (sr.Groups[1].Value != Spartans.NobleSix.Xuid) return Status(HttpStatusCode.NotFound);
            int? category = q.TryGetValue("gameVariantCategory", out var c) ? int.Parse(c) : null;
            bool? ranked = q.TryGetValue("isRanked", out var r) ? r == "true" : null;
            return Ok(World.ServiceRecord(category, ranked));
        }
        if (HistoryPath().Match(path) is { Success: true })
        {
            var type = Enum.Parse<MatchHistoryType>(q["type"]);
            var start = int.Parse(q["start"]);
            var count = int.Parse(q["count"]);
            var entries = World.Games
                .Where(g => type == MatchHistoryType.All || g.Type == type)
                .OrderByDescending(g => g.Entry.MatchInfo.StartTime)
                .Skip(start).Take(count).Select(g => g.Entry).ToList();
            return Ok(new MatchHistory(start, count, entries.Count, entries));
        }
        if (MatchStatsPath().Match(path) is { Success: true } m)
            return World.Games.FirstOrDefault(g => g.Id == m.Groups[1].Value) is { } game ? Ok(game.Stats) : Status(HttpStatusCode.NotFound);
        return Status(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Skill(string path)
    {
        if (MatchSkillPath().Match(path) is { Success: true } m)
            return World.Skill(m.Groups[1].Value) is { } skill
                ? Ok(new SkillResponse<MatchSkill>([new($"xuid({Spartans.NobleSix.Xuid})", 0, skill)]))
                : Status(HttpStatusCode.NotFound);
        if (CsrPath().Match(path) is { Success: true } c)
            return c.Groups[1].Value == HaloWorld.Ids.RankedArena
                ? Ok(new SkillResponse<PlaylistCsr>([new($"xuid({Spartans.NobleSix.Xuid})", 0, HaloWorld.RankedArenaCsr)]))
                : Status(HttpStatusCode.BadRequest);   // unranked playlists answer 400
        return Status(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Discovery(string path)
    {
        if (UgcPath().Match(path) is not { Success: true } m) return Status(HttpStatusCode.NotFound);   // films included
        IReadOnlyList<HaloWorld.Asset> pool = m.Groups[1].Value switch
        {
            "maps" => HaloWorld.Maps,
            "ugcGameVariants" => HaloWorld.Modes,
            _ => HaloWorld.Playlists,
        };
        return pool.FirstOrDefault(a => a.AssetId == m.Groups[2].Value && a.VersionId == m.Groups[3].Value) is { } asset
            ? Ok(asset.Ugc)
            : Status(HttpStatusCode.NotFound);
    }

    // ---- Responses ----------------------------------------------------------------------

    private static HttpResponseMessage Ok<T>(T body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Image()
    {
        var content = new ByteArrayContent(HaloWorld.Png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("") };

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .GroupBy(p => Uri.UnescapeDataString(p[0]))
            .ToDictionary(g => g.Key, g => string.Join(",", g.Select(p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "")));

    [GeneratedRegex(@"^/oban/flight-configurations/titles/hi/audiences/RETAIL/players/xuid\(\d+\)/active$")]
    private static partial Regex FlightPath();
    [GeneratedRegex(@"^/hi/players/xuid\((\d+)\)/Matchmade/servicerecord$")]
    private static partial Regex ServiceRecordPath();
    [GeneratedRegex(@"^/hi/players/xuid\((\d+)\)/matches$")]
    private static partial Regex HistoryPath();
    [GeneratedRegex(@"^/hi/matches/([0-9a-f-]{36})/stats$")]
    private static partial Regex MatchStatsPath();
    [GeneratedRegex(@"^/hi/matches/([0-9a-f-]{36})/skill$")]
    private static partial Regex MatchSkillPath();
    [GeneratedRegex(@"^/hi/playlist/([0-9a-f-]{36})/csrs$")]
    private static partial Regex CsrPath();
    [GeneratedRegex(@"^/hi/(maps|ugcGameVariants|playlists)/([0-9a-f-]{36})/versions/([0-9a-f-]{36})$")]
    private static partial Regex UgcPath();
}
