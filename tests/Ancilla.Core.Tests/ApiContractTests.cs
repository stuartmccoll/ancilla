using System.Reflection;
using System.Text.Json;
using Ancilla.Testing;
using static Ancilla.Testing.Spartans;

namespace Ancilla.Core.Tests;

/// <summary>
/// The API contract: every endpoint the client calls has a hand-written fixture in the real shape,
/// the client parses it, and it holds every field the models need.
/// </summary>
public class ApiContractTests
{
    private static readonly ApiSample Sample = new()
    {
        Xuid = NobleSix.Xuid,
        LatestMatchId = HaloWorld.Ids.Match(117),
        LatestMap = HaloWorld.LiveFire.Ref(2),
        LatestGameVariant = HaloWorld.Oddball.Ref(6),
        LatestPlaylist = HaloWorld.RankedArena.Ref(3),
        CareerTrackPath = HaloWorld.CareerPath,
        RankIconPath = "career_rank/ProgressWidget/1_Recruit.png",
        MedalSheetPath = HaloWorld.Medals.SheetPath,
    };

    public static TheoryData<string> Endpoints => new(ApiContract.Endpoints.Select(e => e.Method));

    private static ApiEndpoint Endpoint(string method) => ApiContract.Endpoints.Single(e => e.Method == method);

    private static async Task<T> Parse<T>(string method)
    {
        var endpoint = Endpoint(method);
        var client = new HaloWaypointClient(new HttpClient(Fixtures.Serving(endpoint.Fixture)), Token);
        return Assert.IsType<T>(await endpoint.Call(client, Sample, CancellationToken.None), exactMatch: false);
    }

    [Fact]
    public void Every_client_endpoint_is_in_the_contract()
    {
        var methods = typeof(HaloWaypointClient).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("Get", StringComparison.Ordinal) && m.Name.EndsWith("Async", StringComparison.Ordinal))
            .Select(m => m.Name).Distinct().Order();
        // A new endpoint needs an entry in ApiContract.Endpoints, a fixture and a parsing test below.
        Assert.Equal(methods, ApiContract.Endpoints.Select(e => e.Method).Order());
    }

    [Fact]
    public void Every_fixture_belongs_to_an_endpoint_and_every_endpoint_has_one()
    {
        var used = ApiContract.Endpoints.Select(e => e.Fixture).Distinct().Order().ToList();
        Assert.Equal(used, Fixtures.Names);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task The_client_reads_the_fixture(string method)
    {
        var endpoint = Endpoint(method);
        var client = new HaloWaypointClient(new HttpClient(Fixtures.Serving(endpoint.Fixture)), Token);
        var result = await endpoint.Call(client, Sample, CancellationToken.None);
        if (endpoint.Model == typeof(FilmManifest))   // read as raw JSON, parsed by Ancilla.Theater
            result = JsonSerializer.Deserialize<FilmManifest>((string)result, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.NotNull(result);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void The_fixture_has_every_field_the_model_needs(string method)
    {
        var endpoint = Endpoint(method);
        if (endpoint.IsImage) return;
        var model = ModelShape.Of(endpoint.Model!);
        var fixture = JsonShape.Parse(Fixtures.Raw(endpoint.Fixture), model.Dictionaries);
        Assert.Empty(ShapeDiff.Compare(model, null, fixture));
        Assert.Empty(ShapeDiff.Compare(model, fixture, fixture));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void Untracked_paths_are_in_the_fixture_and_unread(string method)
    {
        var endpoint = Endpoint(method);
        if (endpoint.Untracked.Count == 0) return;
        var model = ModelShape.Of(endpoint.Model!);
        var fixture = JsonShape.Parse(Fixtures.Raw(endpoint.Fixture), model.Dictionaries);
        foreach (var path in endpoint.Untracked)
        {
            Assert.True(fixture.Contains(path), $"{endpoint.Fixture} has no {path}");
            Assert.DoesNotContain(model.Fields, f => f.Key.Equals(path, StringComparison.OrdinalIgnoreCase)
                || f.Key.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase)
                || f.Key.StartsWith(path + "[", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- What each fixture shows about the real shape ---------------------------------

    [Fact]
    public async Task Profiles_come_in_camel_case()
    {
        var me = await Parse<UserProfile>(nameof(HaloWaypointClient.GetMeAsync));
        Assert.Equal((NobleSix.Xuid, "Noble Six"), (me.Xuid, me.Gamertag));
        Assert.EndsWith("-1080.png", me.Gamerpic!.Xlarge);
        var users = await Parse<List<UserProfile>>(nameof(HaloWaypointClient.GetUsersAsync));
        Assert.Equal([Kat.Xuid, Jorge.Xuid], users.Select(u => u.Xuid));
    }

    [Fact]
    public async Task Clearance_is_the_flight_id() =>
        Assert.Equal(HaloWorld.Ids.Flight, await Parse<string>(nameof(HaloWaypointClient.GetClearanceAsync)));

    [Fact]
    public async Task Reward_track_progress_counts_completed_ranks()
    {
        var career = await Parse<RewardTrackProgress>(nameof(HaloWaypointClient.GetCareerRankAsync));
        Assert.Equal((12, 400, false), (career.CurrentProgress.Rank, career.CurrentProgress.PartialProgress, career.CurrentProgress.HasReachedMaxRank));
        var ops = await Parse<OperationsResponse>(nameof(HaloWaypointClient.GetOperationsAsync));
        Assert.Equal(ops.ActiveOperationRewardTrackPath, ops.OperationRewardTracks[0].RewardTrackPath);
        Assert.True(ops.OperationRewardTracks[1].CurrentProgress.HasReachedMaxRank);
    }

    [Fact]
    public async Task Service_tag_is_inside_the_appearance() =>
        Assert.Equal("B312", (await Parse<AppearanceResponse>(nameof(HaloWaypointClient.GetAppearanceAsync))).Appearance!.ServiceTag);

    [Fact]
    public async Task Track_definitions_have_localised_names()
    {
        var track = await Parse<RewardTrackDefinition>(nameof(HaloWaypointClient.GetRewardTrackDefinitionAsync));
        Assert.Equal("Career Rank", track.Name);
        Assert.Null(track.XpPerRank);
        Assert.Equal(["Recruit", "Recruit", "Cadet"], track.Ranks.Select(r => r.RankTitle));
        Assert.Equal(("Silver", "1", 1000), (track.Ranks[2].RankSubTitle, track.Ranks[2].RankTier, track.Ranks[2].XpRequiredForRank));
    }

    [Fact]
    public async Task Medal_metadata_is_camel_case_with_big_name_ids()
    {
        var medals = await Parse<MedalMetadata>(nameof(HaloWaypointClient.GetMedalMetadataAsync));
        Assert.Equal(4_000_000_001, medals.Medals[0].NameId);   // more than int.MaxValue
        Assert.Equal("Killjoy", medals.Medals[0].Name);
        Assert.Equal(128, medals.Sprites["medium"].Size);
        Assert.Contains("extra-large", medals.Sprites.Keys);
    }

    [Fact]
    public async Task Service_records_keep_their_mode_groups()
    {
        var sr = await Parse<ServiceRecord>(nameof(HaloWaypointClient.GetServiceRecordAsync));
        Assert.Equal((117, 64, 50, 3), (sr.MatchesCompleted, sr.Wins, sr.Losses, sr.Ties));
        Assert.Equal(new TimeSpan(2, 7, 14, 9, 500), IsoDuration.Parse(sr.TimePlayed));
        Assert.NotNull(sr.CoreStats.AverageKDA);
        Assert.Null(sr.CoreStats.KDA);   // per match only
        Assert.Equal(["CaptureTheFlagStats", "EliminationStats", "ExtractionStats", "InfectionStats", "OddballStats", "PveStats",
            "PvpStats", "StockpileStats", "ZonesStats"], sr.ModeStats.Keys.Order());
        Assert.Equal([ApiSample.RankedArena], sr.Subqueries!.PlaylistAssetIds!);
    }

    [Fact]
    public async Task Match_history_entries_carry_their_assets()
    {
        var history = await Parse<MatchHistory>(nameof(HaloWaypointClient.GetMatchHistoryAsync));
        var first = history.Results[0];
        Assert.Equal((MatchOutcome.Win, 18), (first.Outcome, first.MatchInfo.GameVariantCategory));
        Assert.Equal(HaloWorld.LiveFire.AssetId, first.MatchInfo.MapVariant!.AssetId);
        Assert.Equal(ApiSample.RankedArena, first.MatchInfo.Playlist!.AssetId);
        Assert.Equal(MatchOutcome.Loss, history.Results[1].Outcome);
    }

    [Fact]
    public async Task Match_stats_have_humans_bots_and_mode_groups()
    {
        var stats = await Parse<MatchStats>(nameof(HaloWaypointClient.GetMatchStatsAsync));
        var six = stats.Players.Single(p => p.Xuid == NobleSix.Xuid);
        Assert.True(six.IsHuman);
        Assert.Equal(14, six.Stats!.CoreStats.Kills);
        Assert.Contains("OddballStats", six.Stats.ModeStats.Keys);
        Assert.False(stats.IsPve(NobleSix.Xuid));
        Assert.True(stats.Players.Single(p => p.Xuid == Kat.Xuid).ParticipationInfo!.JoinedInProgress);
        var bot = stats.Players.Single(p => !p.IsHuman);
        Assert.Null(bot.Xuid);
        Assert.Equal([MatchOutcome.Win, MatchOutcome.Loss], stats.Teams.Select(t => t.Outcome));
    }

    [Fact]
    public async Task Match_skill_has_csr_change_and_expected_stats()
    {
        var skill = (await Parse<SkillResponse<MatchSkill>>(nameof(HaloWaypointClient.GetMatchSkillAsync))).Value.Single();
        Assert.Equal($"xuid({NobleSix.Xuid})", skill.Id);
        var r = skill.Result!;
        Assert.Equal(("Diamond 5", "Diamond 6"), (r.RankRecap!.PreMatchCsr!.Display, r.RankRecap.PostMatchCsr!.Display));
        Assert.Equal(11.6, r.StatPerformances!["Kills"].Expected);
        Assert.Equal(["0", "1"], r.TeamMmrs!.Keys.Order());
    }

    [Fact]
    public async Task Playlist_csr_has_current_season_and_all_time()
    {
        var csr = (await Parse<SkillResponse<PlaylistCsr>>(nameof(HaloWaypointClient.GetPlaylistCsrAsync))).Value.Single().Result!;
        Assert.Equal(("Diamond 6", "Diamond 6", "Onyx 1612"), (csr.Current.Display, csr.SeasonMax.Display, csr.AllTimeMax.Display));
    }

    [Theory]
    [InlineData(nameof(HaloWaypointClient.GetMapAsync), "Live Fire", true)]
    [InlineData(nameof(HaloWaypointClient.GetGameVariantAsync), "Oddball", false)]
    [InlineData(nameof(HaloWaypointClient.GetPlaylistAsync), "Ranked Arena", false)]
    public async Task Ugc_assets_list_their_files(string method, string name, bool hasNavMesh)
    {
        var asset = await Parse<UgcAsset>(method);
        Assert.Equal(name, asset.PublicName);
        Assert.StartsWith("https://blobs-infiniteugc.svc.halowaypoint.com/", asset.ThumbnailUrl);
        Assert.EndsWith("images/hero.jpg", asset.HeroUrl);
        Assert.Equal(hasNavMesh, asset.BlobFileUri(UgcAsset.NavMeshFile) is not null);
    }

    [Fact]
    public async Task Film_manifests_list_their_chunks()
    {
        var raw = await Parse<string>(nameof(HaloWaypointClient.GetFilmManifestJsonAsync));
        var manifest = JsonSerializer.Deserialize<FilmManifest>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(manifest.CustomData.HasSummary);
        Assert.Equal([1, 2, 3], manifest.CustomData.Chunks.Select(c => c.ChunkType));
        Assert.StartsWith("https://blobs-infiniteugc.svc.halowaypoint.com/", manifest.BlobStoragePathPrefix);
    }

    [Theory]
    [InlineData(nameof(HaloWaypointClient.GetCmsImageAsync))]
    [InlineData(nameof(HaloWaypointClient.GetWaypointFileAsync))]
    public async Task Images_come_back_with_their_type(string method)
    {
        var (bytes, type) = await Parse<ValueTuple<byte[], string>>(method);
        Assert.Equal("image/png", type);
        Assert.Equal(HaloWorld.Png, bytes);
    }

    [Fact]
    public void Fixtures_write_xuids_as_placeholders()
    {
        // So the privacy check never sees a decimal XUID; the loader fills them in.
        var json = Fixtures.Names.Where(n => n.EndsWith(".json", StringComparison.Ordinal)).ToList();
        Assert.All(json, n => Assert.DoesNotMatch(@"\d{15,}", Fixtures.Raw(n)));
        Assert.Contains("{xuid:NobleSix}", Fixtures.Raw("match-stats.json"));
        Assert.Contains($"xuid({NobleSix.Xuid})", Fixtures.Json("match-stats.json"));
    }
}
