using System.Text.Json;

namespace Ancilla.Core.Tests;

public class ModelsTests
{
    [Theory]
    [InlineData(1452, "Diamond", 5, "Diamond 6")]
    [InlineData(950, "Platinum", 1, "Platinum 2")]
    [InlineData(1612, "Onyx", 0, "Onyx 1612")]
    public void Csr_display_uses_one_based_sub_tiers(int value, string tier, int sub, string shown) =>
        Assert.Equal(shown, new Csr(value, 0, tier, 0, sub, null, 0, 0, 5).Display);

    [Fact]
    public void Csr_without_a_tier_is_unranked()
    {
        Assert.False(new Csr(-1, 5, "", 0, 0, null, 0, 0, 5).IsRanked);
        Assert.Equal("Unranked", new Csr(1200, 0, null, 0, 0, null, 0, 0, 5).Display);
    }

    [Fact]
    public void Players_know_their_xuid_and_team_stats()
    {
        StatsBlock Block(int kills) => new() { CoreStats = new CoreStats { Kills = kills } };
        var spartan = new MatchPlayer("xuid(117)", 1, 1, MatchOutcome.Win, 1, null, [new(0, Block(3)), new(1, Block(9))]);
        Assert.Equal("117", spartan.Xuid);
        Assert.True(spartan.IsHuman);
        Assert.Equal(9, spartan.Stats!.CoreStats.Kills);   // the team they finished on

        var bot = new MatchPlayer("bid(2001.0)", 2, 5, MatchOutcome.Loss, 2, null, [new(0, Block(4))]);
        Assert.Null(bot.Xuid);
        Assert.False(bot.IsHuman);
        Assert.Equal(4, bot.Stats!.CoreStats.Kills);         // falls back to the only block

        Assert.Null(new MatchPlayer("xuid(1)", 1, 0, MatchOutcome.Win, 1, null, []).Stats);
    }

    [Fact]
    public void Kill_death_ratio_with_no_deaths_is_the_kills()
    {
        Assert.Equal(7, new CoreStats { Kills = 7 }.KillDeathRatio);
        Assert.Equal(2.5, new CoreStats { Kills = 5, Deaths = 2 }.KillDeathRatio);
    }

    private const string Blobs = "https://blobs-infiniteugc.svc.halowaypoint.com/ugcstorage/map/1/2/";

    [Fact]
    public void Ugc_file_urls_come_only_from_listed_files()
    {
        var map = new UgcAsset("a", "v", "Blood Gulch", null, new UgcFiles(Blobs, ["images/thumbnail.jpg", UgcAsset.NavMeshFile]));
        Assert.Equal(Blobs + "images/thumbnail.jpg", map.ThumbnailUrl);
        Assert.Equal(map.ThumbnailUrl, map.HeroUrl);   // no hero image listed
        Assert.Equal(new Uri(Blobs + UgcAsset.NavMeshFile), map.BlobFileUri(UgcAsset.NavMeshFile));
        Assert.Null(map.BlobFileUri("images/hero.jpg"));
        Assert.Null((map with { Files = null }).ThumbnailUrl);
    }

    [Theory]
    [InlineData("http://blobs-infiniteugc.svc.halowaypoint.com/x/")]
    [InlineData("https://evil.example/x/")]
    [InlineData("https://blobs-infiniteugc.svc.halowaypoint.com.evil.example/x/")]
    [InlineData("https://blobs-infiniteugc.svc.halowaypoint.com/x/?sig=")]
    public void Blob_urls_must_be_on_the_public_blob_host(string prefix)
    {
        var map = new UgcAsset("a", "v", "Forge World", null, new UgcFiles(prefix, [UgcAsset.NavMeshFile]));
        Assert.Null(map.BlobFileUri(UgcAsset.NavMeshFile));
    }

    [Fact]
    public void Service_records_keep_unknown_mode_groups()
    {
        const string json = """
            {"TimePlayed":"PT1H","MatchesCompleted":3,"Wins":2,"Losses":1,"Ties":0,
             "CoreStats":{"Kills":30,"Deaths":20,"Assists":9,"AverageKDA":4.0,"Medals":[{"NameId":4000000001,"Count":2,"TotalPersonalScoreAwarded":0}]},
             "Subqueries":{"PlaylistAssetIds":["edfef3ac-9cbe-4fa2-b949-8f29deafd483"]},
             "GruntBirthdayPartyStats":{"Confetti":117}}
            """;
        var sr = JsonSerializer.Deserialize<ServiceRecord>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(3, sr.MatchesCompleted);
        Assert.Equal(4_000_000_001, sr.CoreStats.Medals[0].NameId);   // bigger than int
        Assert.Equal(117, sr.ModeStats["GruntBirthdayPartyStats"].GetProperty("Confetti").GetInt32());
        Assert.Single(sr.Subqueries!.PlaylistAssetIds!);
    }

    [Fact]
    public void Report_lists_passes_unfinished_first()
    {
        TrackStatus P(string n, bool owned, bool done, bool active = false) => new(n, n, 0, 10, done ? 10_000 : 500, 10_000, owned, done, active);
        var passes = new[] { P("finished", true, true), P("active", true, false, true), P("unowned", false, false) };
        var r = new ProgressReport("Noble Six", 10, new CareerStatus(0, 1, "", null, 0, 1, false), passes, new XpPerMatchEstimate(100, ""), []);

        Assert.Equal(["active"], r.RemainingPasses(includeUnowned: false).Select(p => p.Name));
        Assert.Equal(["active", "unowned"], r.RemainingPasses(includeUnowned: true).Select(p => p.Name));
        Assert.Equal(["active", "unowned", "finished"], r.ListedPasses(true, true).Select(p => p.Name));
        Assert.Equal("active", r.ActivePass!.Name);
        Assert.Equal(0, r.GamesFor(0));
        Assert.Equal(3, r.GamesFor(201));
        Assert.Equal(0, r.PassGamesFor(-5));
    }
}
