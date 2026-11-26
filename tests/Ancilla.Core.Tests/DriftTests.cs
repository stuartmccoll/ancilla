using System.Text.Json;
using System.Text.Json.Serialization;
using Ancilla.Testing;
using static Ancilla.Testing.Spartans;

namespace Ancilla.Core.Tests;

public class JsonShapeTests
{
    [Fact]
    public void Shapes_hold_paths_and_kinds_not_values()
    {
        var shape = JsonShape.Parse("""
            {"Name":"Noble Six","Kills":14,"Kda":2.5,"Ranked":true,"Gone":null,
             "Players":[{"Rank":1},{"Rank":2,"Left":true}],"Empty":[]}
            """);
        Assert.Equal(JsonKind.Object, shape[""]);
        Assert.Equal(JsonKind.String, shape["Name"]);
        Assert.Equal(JsonKind.Integer, shape["Kills"]);
        Assert.Equal(JsonKind.Fraction, shape["Kda"]);
        Assert.Equal(JsonKind.Boolean, shape["Ranked"]);
        Assert.Equal(JsonKind.Null, shape["Gone"]);
        Assert.Equal(JsonKind.Object, shape["Players[]"]);   // every element merged into one
        Assert.Equal(JsonKind.Boolean, shape["players[].left"]);   // paths ignore case
        Assert.False(shape.Contains("Empty[]"));
        Assert.DoesNotContain(shape.Fields, f => f.Key.Contains("Noble"));
    }

    [Theory]
    [InlineData("xuid(117)")]
    [InlineData("edfef3ac-9cbe-4fa2-b949-8f29deafd483")]
    [InlineData("0")]
    [InlineData("fr-FR")]
    [InlineData("Noble Six")]
    public void Keys_that_arent_field_names_become_a_star(string key)
    {
        var shape = JsonShape.Parse(JsonSerializer.Serialize(new Dictionary<string, object> { ["Teams"] = new Dictionary<string, int> { [key] = 1 } }));
        Assert.Equal(["", "Teams", "Teams.*"], shape.Fields.Select(f => f.Key));
    }

    [Fact]
    public void Known_dictionaries_collapse_their_keys()
    {
        var shape = JsonShape.Parse("""{"Sprites":{"small":{"Size":72},"medium":{"Size":128}}}""", new HashSet<string> { "Sprites" });
        Assert.Equal(["", "Sprites", "Sprites.*", "Sprites.*.Size"], shape.Fields.Select(f => f.Key));
    }

    [Theory]
    [InlineData("A.B", "A")]
    [InlineData("A[]", "A")]
    [InlineData("A[].B.*", "A[].B")]
    [InlineData("A", "")]
    [InlineData("[]", "")]
    public void Parents_go_one_level_up(string path, string parent) => Assert.Equal(parent, JsonShape.Parent(path));

    private sealed class Sample
    {
        public int Count { get; init; }
        public double Ratio { get; init; }
        public string? Note { get; init; }
        public MatchOutcome Outcome { get; init; }
        public List<Inner> Items { get; init; } = [];
        public Dictionary<string, Inner>? ByName { get; init; }
        [JsonConverter(typeof(LocalizedStringConverter))] public string? Title { get; init; }
        [JsonIgnore] public int Ignored { get; init; }
        public int Computed => Count * 2;
        [JsonExtensionData] public Dictionary<string, JsonElement> Rest { get; init; } = [];
    }

    private sealed record Inner(string Id, int? Score, DateTimeOffset When);

    [Fact]
    public void Models_describe_what_deserialising_reads()
    {
        var m = ModelShape.Of(typeof(Sample));
        Assert.Equal(["", "Count", "Ratio", "Note", "Outcome", "Items", "Items[]", "Items[].Id", "Items[].Score", "Items[].When",
            "ByName", "ByName.*", "ByName.*.Id", "ByName.*.Score", "ByName.*.When", "Title"], m.Fields.Select(f => f.Key));
        Assert.Equal(JsonKind.Integer, m["Count"]);
        Assert.Equal(JsonKind.Number, m["Ratio"]);
        Assert.Equal(JsonKind.String | JsonKind.Null, m["Note"]);
        Assert.Equal(JsonKind.Integer, m["Outcome"]);
        Assert.Equal(JsonKind.String, m["Items[].Id"]);
        Assert.Equal(JsonKind.Integer | JsonKind.Null, m["Items[].Score"]);
        Assert.Equal(JsonKind.String, m["Items[].When"]);
        Assert.Equal(JsonKind.Object | JsonKind.Null, m["ByName"]);
        Assert.Equal(JsonKind.String | JsonKind.Object | JsonKind.Null, m["Title"]);
        Assert.Contains("ByName", m.Dictionaries);
    }
}

public class ShapeDiffTests
{
    private static readonly ModelShape Model = ModelShape.Of(typeof(MatchHistory));

    private static string Entry(string extra = "", string info = """{"StartTime":"2026-11-20T19:00:00Z","EndTime":"2026-11-20T19:12:00Z","GameVariantCategory":6,"TeamsEnabled":true,"ClearanceId":null}""") =>
        $$"""{"MatchId":"m","MatchInfo":{{info}},"LastTeamId":0,"Outcome":2,"Rank":1,"PresentAtEndOfMatch":true{{extra}}}""";

    private static JsonShape History(params string[] entries) =>
        JsonShape.Parse($$"""{"Start":0,"Count":25,"ResultCount":{{entries.Length}},"Results":[{{string.Join(",", entries)}}]}""", Model.Dictionaries);

    private static IReadOnlyList<DriftItem> Diff(JsonShape live, JsonShape? known = null) => ShapeDiff.Compare(Model, known, live);

    [Fact]
    public void The_same_shape_has_no_drift() => Assert.Empty(Diff(History(Entry()), History(Entry())));

    [Fact]
    public void A_missing_field_the_model_needs_breaks()
    {
        var item = Assert.Single(Diff(History(Entry().Replace("\"Rank\":1,", ""))));
        Assert.Equal((DriftKind.Missing, "Results[].Rank", true), (item.Kind, item.Path, item.Breaks));
    }

    [Fact]
    public void A_missing_nullable_field_is_optional() =>
        Assert.Empty(Diff(History(Entry(info: """{"StartTime":"2026-11-20T19:00:00Z","EndTime":"2026-11-20T19:12:00Z","GameVariantCategory":6,"TeamsEnabled":true}"""))));

    [Fact]
    public void Nothing_is_missing_from_an_empty_list() => Assert.Empty(Diff(History()));

    [Theory]
    [InlineData("\"Outcome\":2", "\"Outcome\":\"Win\"", "Results[].Outcome")]
    [InlineData("\"Rank\":1", "\"Rank\":1.5", "Results[].Rank")]
    [InlineData("\"MatchId\":\"m\"", "\"MatchId\":null", "Results[].MatchId")]
    public void A_kind_the_model_cant_take_breaks(string from, string to, string path)
    {
        var item = Assert.Single(Diff(History(Entry().Replace(from, to))));
        Assert.Equal((DriftKind.Retyped, path, true), (item.Kind, item.Path, item.Breaks));
    }

    [Fact]
    public void Changes_to_fields_ancilla_doesnt_read_are_reported_against_the_fixture()
    {
        var known = History(Entry(",\"Lobby\":7,\"Old\":true"));
        var live = History(Entry(",\"Lobby\":\"seven\",\"New\":{\"A\":1,\"B\":[{\"C\":2}]}"));
        var items = Diff(live, known);
        Assert.All(items, i => Assert.False(i.Breaks));
        Assert.Equal(
            [(DriftKind.Retyped, "Results[].Lobby"), (DriftKind.Added, "Results[].New"), (DriftKind.Missing, "Results[].Old")],
            items.Select(i => (i.Kind, i.Path)));
        Assert.Equal("an object, 4 more fields inside", items[1].Detail);
    }

    [Fact]
    public void Nulls_and_whole_numbers_arent_a_change_of_kind()
    {
        var known = History(Entry(",\"Lobby\":7.5,\"Name\":\"x\""));
        Assert.Empty(Diff(History(Entry(",\"Lobby\":7,\"Name\":null")), known));
    }

    [Fact]
    public void Stat_groups_of_other_modes_arent_missing_but_new_ones_are_added()
    {
        var model = ModelShape.Of(typeof(StatsBlock));
        Assert.Contains("", model.Open);
        var known = JsonShape.Parse("""{"CoreStats":{"Kills":1},"OddballStats":{"SkullGrabs":2},"PvpStats":{"Kills":1}}""");
        var live = JsonShape.Parse("""{"CoreStats":{"Kills":1},"PveStats":{"BossKills":3},"PvpStats":{}}""");
        var items = ShapeDiff.Compare(model, known, live).Where(i => !i.Breaks).ToList();   // CoreStats is cut down here
        // OddballStats isn't missing (another mode's group), but a field going from a group that's there is.
        Assert.Equal([(DriftKind.Added, "PveStats"), (DriftKind.Missing, "PvpStats.Kills")], items.Select(i => (i.Kind, i.Path)));
        Assert.Equal(["PveStats.BossKills: a whole number"], items[0].Inside!);
    }

    [Fact]
    public void Nothing_inside_an_untracked_path_is_compared()
    {
        var known = History(Entry(",\"Settings\":{\"A\":1,\"B\":2}"));
        var live = History(Entry(",\"Settings\":{\"B\":\"two\",\"C\":[3]}"));
        Assert.Equal(3, Diff(live, known).Count);
        Assert.Empty(ShapeDiff.Compare(Model, known, live, ["Results[].Settings"]));
        // The untracked object itself is still compared.
        var item = Assert.Single(ShapeDiff.Compare(Model, known, History(Entry()), ["Results[].Settings"]));
        Assert.Equal((DriftKind.Missing, "Results[].Settings"), (item.Kind, item.Path));
    }

    [Fact]
    public void An_optional_field_ancilla_reads_going_missing_is_noted()
    {
        var item = Assert.Single(Diff(History(Entry(info: """{"StartTime":"2026-11-20T19:00:00Z","EndTime":"2026-11-20T19:12:00Z","GameVariantCategory":6,"TeamsEnabled":true}""")),
            History(Entry())));
        Assert.Equal((DriftKind.Missing, "Results[].MatchInfo.ClearanceId", false), (item.Kind, item.Path, item.Breaks));
    }
}

public class ApiDriftTests
{
    private static async Task<DriftReport> RunAgainst(HttpMessageHandler backend, Func<string, string?>? fixture = null)
    {
        var recorder = new ResponseRecorder { InnerHandler = backend };
        var http = new HttpClient(recorder);
        var sample = await ApiSample.DiscoverAsync(new HaloWaypointClient(http, Token));
        return await ApiDrift.RunAsync(new HaloWaypointClient(http, Token), recorder, sample, fixture ?? Fixtures.Raw, new Redactor());
    }

    [Fact]
    public async Task The_fake_backend_has_nothing_that_breaks_ancilla()
    {
        var halo = new FakeHalo();
        var report = await RunAgainst(halo.CreateHandler());

        Assert.Equal(ApiContract.Endpoints.Count, report.Endpoints.Count);
        // The fake only has films for custom games, and the check uses the latest matchmade match.
        var film = report.Endpoints.Single(e => e.Endpoint.Model == typeof(FilmManifest));
        Assert.StartsWith("404", film.Failure);
        Assert.All(report.Endpoints.Where(e => e != film), e =>
        {
            Assert.Null(e.Skipped);
            Assert.Null(e.Failure);
            Assert.DoesNotContain(e.Items, i => i.Breaks);
        });
        // The fake serves what the models hold, not all the real shape: those differences are only notes.
        Assert.Contains(report.Endpoints, e => e.Items.Count > 0);
    }

    [Fact]
    public async Task Fixtures_served_as_live_responses_have_no_drift()
    {
        var report = await RunAgainst(new FixtureBackend());
        Assert.False(report.Breaks);
        Assert.All(report.Endpoints, e => Assert.Empty(e.Items));
        Assert.EndsWith("Every response has the shape its fixture has." + Environment.NewLine, report.Format());
    }

    [Fact]
    public async Task The_report_names_fields_never_values()
    {
        // Match stats where Rank became text and a XUID-keyed object appeared.
        var backend = new FixtureBackend
        {
            Override = ("match-stats.json", Fixtures.Json("match-stats.json")
                .Replace("\"Rank\": 1,", "\"Rank\": \"first\",")
                .Replace("\"MatchId\":", $"\"Lobby\": {{\"xuid({Kat.Xuid})\": {{\"Gamertag\": \"Kat B320\"}}}},\n  \"MatchId\":")),
        };
        var report = await RunAgainst(backend);
        var text = report.Format();

        Assert.True(report.Breaks);
        var stats = report.Endpoints.Single(e => e.Endpoint.Fixture == "match-stats.json");
        Assert.Contains(stats.Items, i => i is { Kind: DriftKind.Retyped, Path: "Players[].Rank", Breaks: true });
        Assert.Contains(stats.Items, i => i is { Kind: DriftKind.Added, Path: "Lobby", Breaks: false });
        Assert.Contains("Could break Ancilla:", text);
        Assert.Contains("retyped  Players[].Rank: Ancilla takes a whole number, now a whole number or text", text);
        Assert.Contains("added    Lobby: an object, 2 more fields inside", text);
        Assert.DoesNotContain("Lobby.*.Gamertag", text);
        var detail = report.Format(detail: true);
        Assert.Contains("Lobby.*.Gamertag: text", detail);   // the key, a XUID, became *
        Assert.DoesNotContain(Kat.Xuid, detail);
        Assert.DoesNotContain("Kat B320", detail);
        Assert.DoesNotContain(Kat.Xuid, text);
        Assert.DoesNotContain("Kat B320", text);
        Assert.DoesNotContain("first", text);
        Assert.DoesNotContain("Noble Six", text);
    }

    private static string HistoryEntry(string id, int category) =>
        $$"""{"MatchId":"{{id}}","MatchInfo":{"StartTime":"2026-11-20T19:00:00Z","EndTime":"2026-11-20T19:12:00Z","GameVariantCategory":{{category}},"TeamsEnabled":true,"ClearanceId":null},"LastTeamId":0,"Outcome":2,"Rank":1,"PresentAtEndOfMatch":true}""";

    [Fact]
    public async Task The_sample_match_isnt_firefight_when_theres_a_choice()
    {
        var history = $$"""{"Start":0,"Count":25,"ResultCount":2,"Results":[{{HistoryEntry(HaloWorld.Ids.Match(1), GameVariantCategories.Firefight)}},{{HistoryEntry(HaloWorld.Ids.Match(2), 18)}}]}""";
        var backend = new FixtureBackend { Override = ("match-history.json", history) };
        var sample = await ApiSample.DiscoverAsync(new HaloWaypointClient(new HttpClient(backend), Token));
        Assert.Equal(HaloWorld.Ids.Match(2), sample.MatchId);
        Assert.Equal(sample.MatchId, sample.SkillMatchId);
    }

    [Fact]
    public async Task Skill_is_skipped_when_every_match_is_firefight()
    {
        var history = Fixtures.Json("match-history.json")
            .Replace("\"GameVariantCategory\": 18", $"\"GameVariantCategory\": {GameVariantCategories.Firefight}");
        var report = await RunAgainst(new FixtureBackend { Override = ("match-history.json", history) });
        var skill = report.Endpoints.Single(e => e.Endpoint.Fixture == "match-skill.json");
        Assert.StartsWith("your last 25 matchmade matches were all Firefight", skill.Skipped);
        Assert.Null(report.Endpoints.Single(e => e.Endpoint.Fixture == "match-stats.json").Skipped);
        Assert.False(report.Breaks);
    }

    [Fact]
    public async Task Endpoints_without_ids_to_call_them_with_are_skipped()
    {
        var recorder = new ResponseRecorder { InnerHandler = new FixtureBackend() };
        var http = new HttpClient(recorder);
        var sample = new ApiSample { Xuid = NobleSix.Xuid };
        var report = await ApiDrift.RunAsync(new HaloWaypointClient(http, Token), recorder, sample, Fixtures.Raw, new Redactor());
        var stats = report.Endpoints.Single(e => e.Endpoint.Fixture == "match-stats.json");
        Assert.Equal("no matchmade match in your history", stats.Skipped);
        Assert.False(stats.Breaks);
        Assert.Contains("skipped: no matchmade match in your history", report.Format());
    }

    [Fact]
    public async Task Failed_endpoints_break_and_keep_ids_out_of_the_report()
    {
        var halo = new FakeHalo().Fail("/Matchmade/servicerecord", System.Net.HttpStatusCode.NotFound);
        var report = await RunAgainst(halo.CreateHandler());
        var sr = report.Endpoints.Single(e => e.Endpoint.Fixture == "service-record.json");
        Assert.StartsWith("404 Not Found", sr.Failure);
        Assert.DoesNotContain(NobleSix.Xuid, report.Format());
        Assert.True(report.Breaks);
    }

    /// <summary>Answers each request with its endpoint's fixture, picked by host and path.</summary>
    private sealed class FixtureBackend : HttpMessageHandler
    {
        public (string Name, string Json)? Override { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = FixtureFor(request.RequestUri!);
            if (Override is { } o && o.Name == name)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new StringContent(o.Json, System.Text.Encoding.UTF8, "application/json") });
            using var inner = new HttpMessageInvoker(Fixtures.Serving(name));
            return inner.SendAsync(request, ct);
        }

        private static string FixtureFor(Uri uri)
        {
            var path = uri.AbsolutePath;
            return (uri.Host.Split('.')[0], path) switch
            {
                ("profile", "/users/me") => "profile-me.json",
                ("profile", _) => "profile-users.json",
                ("settings", _) => "flight.json",
                ("economy", _) when path.EndsWith("careerrank1") => "career-rank.json",
                ("economy", _) when path.EndsWith("operations") => "operations.json",
                ("economy", _) => "appearance.json",
                ("gamecms-hacs", _) when path.Contains("/Progression/") => "reward-track.json",
                ("gamecms-hacs", _) when path.EndsWith("metadata.json") => "medal-metadata.json",
                ("gamecms-hacs", _) => "image.png",
                ("halostats", _) when path.EndsWith("servicerecord") => "service-record.json",
                ("halostats", _) when path.EndsWith("/matches") => "match-history.json",
                ("halostats", _) => "match-stats.json",
                ("skill", _) when path.EndsWith("/skill") => "match-skill.json",
                ("skill", _) => "playlist-csr.json",
                ("discovery-infiniteugc", _) when path.Contains("/films/") => "film-spectate.json",
                ("discovery-infiniteugc", _) when path.StartsWith("/hi/maps/") => "ugc-map.json",
                ("discovery-infiniteugc", _) when path.StartsWith("/hi/ugcGameVariants/") => "ugc-game-variant.json",
                _ => "ugc-playlist.json",
            };
        }
    }
}
