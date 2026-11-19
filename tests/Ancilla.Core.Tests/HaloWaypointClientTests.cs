using System.Net;
using Ancilla.Testing;
using static Ancilla.Testing.Spartans;

namespace Ancilla.Core.Tests;

public class HaloWaypointClientTests
{
    private readonly FakeHalo _halo = new();
    private HaloWaypointClient Client(string token = Token) => new(_halo.CreateClient(), token);

    [Theory]
    [InlineData("v4=cortana-synthetic-token")]
    [InlineData("cortana-synthetic-token")]
    [InlineData("  \"v4=cortana-synthetic-token\"  ")]
    [InlineData("v4%3Dcortana-synthetic-token")]
    public void Tokens_are_normalised_to_the_v4_form(string pasted) =>
        Assert.Equal(Token, HaloWaypointClient.NormaliseToken(pasted));

    [Fact]
    public async Task Every_request_carries_the_token_header_and_a_user_agent()
    {
        var me = await Client("cortana-synthetic-token").GetMeAsync();
        Assert.Equal(NobleSix.Gamertag, me.Gamertag);
        Assert.Equal(NobleSix.Xuid, me.Xuid);
        var req = Assert.Single(_halo.Requests);
        Assert.Equal(Token, req.Headers["x-343-authorization-spartan"]);
        Assert.StartsWith("Ancilla/", req.Headers["User-Agent"]);
        Assert.Equal(HttpMethod.Get, req.Method);
    }

    [Fact]
    public async Task Economy_calls_get_a_clearance_that_is_fetched_once()
    {
        var client = Client();
        await client.GetCareerRankAsync(NobleSix.Xuid);
        await client.GetOperationsAsync(NobleSix.Xuid);

        var flights = _halo.Requests.Where(r => r.Uri.Host.StartsWith("settings.")).ToList();
        Assert.Single(flights);
        Assert.All(_halo.Requests.Where(r => r.Uri.Host.StartsWith("economy.")),
            r => Assert.Equal(HaloWorld.Ids.Flight, r.Headers["343-clearance"]));
        Assert.DoesNotContain(_halo.Requests.Where(r => !r.Uri.Host.StartsWith("economy.")), r => r.Headers.ContainsKey("343-clearance"));
    }

    [Fact]
    public async Task Clearance_falls_back_to_the_latest_match()
    {
        _halo.Fail("settings.svc", HttpStatusCode.ServiceUnavailable);
        var clearance = await Client().GetClearanceAsync(NobleSix.Xuid);
        Assert.Equal(HaloWorld.Ids.Flight, clearance);
        Assert.Contains(_halo.Requests, r => r.Uri.AbsolutePath.EndsWith("/matches") && r.Uri.Query.Contains("count=1"));
    }

    [Fact]
    public async Task A_rejected_token_explains_itself_without_repeating_the_token()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Client("v4=covenant-forgery").GetMeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("Spartan token missing or expired", ex.Message);
        Assert.Contains("Spartan token is not valid.", ex.Message);    // first line of x-343-exception only
        Assert.DoesNotContain("second line", ex.Message);
        Assert.DoesNotContain("covenant-forgery", ex.Message);
    }

    [Fact]
    public async Task Other_failures_carry_their_status()
    {
        _halo.Fail("/stats", HttpStatusCode.InternalServerError);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Client().GetMatchStatsAsync(HaloWorld.Ids.Match(0)));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.StartsWith("500 ", ex.Message);
        Assert.DoesNotContain("expired", ex.Message);
    }

    [Fact]
    public async Task Bad_ids_are_refused_before_any_request()
    {
        var c = Client();
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetMatchStatsAsync("../../users/me"));
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetServiceRecordAsync("me"));
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetMatchHistoryAsync("1)/x"));
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetMatchSkillAsync(HaloWorld.Ids.Match(0), ["1", "x"]));
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetPlaylistCsrAsync("ranked", NobleSix.Xuid));
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetMapAsync(new AssetRef(2, HaloWorld.Ids.Map(1), "v1")));
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetUsersAsync([NobleSix.Xuid, "Arbiter"]));
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetFilmManifestJsonAsync("not-a-match"));
        await Assert.ThrowsAsync<ArgumentException>(() => c.GetCareerRankAsync(""));
        Assert.Empty(_halo.Requests);
    }

    [Theory]
    [InlineData(null, null, "")]
    [InlineData(42, null, "?gameVariantCategory=42")]
    [InlineData(null, true, "?isRanked=true")]
    [InlineData(18, false, "?gameVariantCategory=18&isRanked=false")]
    public async Task Service_record_filters_become_query_parameters(int? category, bool? ranked, string query)
    {
        await Client().GetServiceRecordAsync(NobleSix.Xuid, category, ranked);
        Assert.Equal(query, Assert.Single(_halo.Requests).Uri.Query);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10, 10)]
    [InlineData(100, 25)]
    public async Task Match_history_pages_are_capped_at_25(int asked, int sent)
    {
        await Client().GetMatchHistoryAsync(NobleSix.Xuid, 0, asked, MatchHistoryType.Custom);
        var q = Assert.Single(_halo.Requests).Uri.Query;
        Assert.Contains($"count={sent}", q);
        Assert.Contains("type=Custom", q);
    }

    [Fact]
    public async Task Reads_match_stats_with_mode_groups()
    {
        var game = _halo.World.Games.First(g => g.Category == 18);   // Oddball
        var stats = await Client().GetMatchStatsAsync(game.Id);
        var me = stats.Players.Single(p => p.Xuid == NobleSix.Xuid);
        Assert.Equal(game.Me.Stats!.CoreStats.Kills, me.Stats!.CoreStats.Kills);
        Assert.Contains("OddballStats", me.Stats.ModeStats.Keys);
        Assert.Contains("PvpStats", me.Stats.ModeStats.Keys);
        Assert.Equal(8, stats.Players.Count);
        Assert.Equal(2, stats.Teams.Count);
    }

    [Fact]
    public async Task Skill_and_users_send_every_player()
    {
        var c = Client();
        await c.GetMatchSkillAsync(HaloWorld.Ids.Match(0), [NobleSix.Xuid, Kat.Xuid]);
        await c.GetUsersAsync([Kat.Xuid, Jorge.Xuid]);
        var reqs = _halo.Requests.ToArray();
        Assert.Equal($"?players=xuid({NobleSix.Xuid})&players=xuid({Kat.Xuid})", reqs[0].Uri.Query);
        Assert.Equal($"?xuids={Kat.Xuid},{Jorge.Xuid}", reqs[1].Uri.Query);
    }

    [Fact]
    public async Task Reads_ugc_csr_medals_and_images()
    {
        var c = Client();
        Assert.Equal("Live Fire", (await c.GetMapAsync(HaloWorld.LiveFire.Ref(2))).PublicName);
        Assert.Equal("Oddball", (await c.GetGameVariantAsync(HaloWorld.Oddball.Ref(6))).PublicName);
        Assert.Equal("Ranked Arena", (await c.GetPlaylistAsync(HaloWorld.RankedArena.Ref(3))).PublicName);
        var csr = await c.GetPlaylistCsrAsync(HaloWorld.Ids.RankedArena, NobleSix.Xuid);
        Assert.Equal("Diamond 6", csr.Value[0].Result!.Current.Display);
        var medals = await c.GetMedalMetadataAsync();
        Assert.Contains(medals.Medals, m => m.Name == "Killjoy");
        var (bytes, type) = await c.GetWaypointFileAsync(HaloWorld.Medals.SheetPath);
        Assert.Equal("image/png", type);
        Assert.Equal(HaloWorld.Png, bytes);
        Assert.Equal("image/png", (await c.GetCmsImageAsync("career_rank/ProgressWidget/1_Rank.png")).ContentType);
    }

    [Fact]
    public async Task Unranked_playlists_fail_with_400()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Client().GetPlaylistCsrAsync(HaloWorld.Ids.Playlist(2), NobleSix.Xuid));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }
}
