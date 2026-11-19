using System.Text.Json;
using Ancilla.Testing;

namespace Ancilla.Core.Tests;

public class ServiceRecordMathTests
{
    private static readonly HaloWorld World = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Everything_minus_firefight_matches_the_pvp_matches()
    {
        var all = World.ServiceRecord();
        var firefight = World.ServiceRecord(GameVariantCategories.Firefight);
        var pvp = ServiceRecordMath.Subtract(all, firefight, "PveStats");

        var expected = World.Matchmade().Where(g => !g.IsPve).Select(g => g.Me.Stats!.CoreStats).ToList();
        Assert.Equal(expected.Count, pvp.MatchesCompleted);
        Assert.Equal(expected.Sum(c => c.Kills), pvp.CoreStats.Kills);
        Assert.Equal(expected.Sum(c => c.Deaths), pvp.CoreStats.Deaths);
        Assert.Equal(expected.Sum(c => c.Assists), pvp.CoreStats.Assists);
        Assert.Equal(expected.Sum(c => c.DamageDealt), pvp.CoreStats.DamageDealt);
        Assert.Equal(100.0 * expected.Sum(c => c.ShotsHit) / expected.Sum(c => c.ShotsFired), pvp.CoreStats.Accuracy, 9);
        Assert.Equal(all.Wins - firefight.Wins, pvp.Wins);
        Assert.DoesNotContain("PveStats", pvp.ModeStats.Keys);
        Assert.Contains("PvpStats", pvp.ModeStats.Keys);
    }

    [Fact]
    public void Average_kda_is_recalculated_from_the_totals()
    {
        var total = new ServiceRecord { MatchesCompleted = 10, CoreStats = new CoreStats { Kills = 100, Deaths = 50, Assists = 30, AverageKDA = 99 } };
        var part = new ServiceRecord { MatchesCompleted = 5, CoreStats = new CoreStats { Kills = 70, Deaths = 10, Assists = 0 } };
        var r = ServiceRecordMath.Subtract(total, part);
        // (30 + 30/3 - 40) / 5
        Assert.Equal(0, r.CoreStats.AverageKDA!.Value, 9);
        Assert.Equal(5, r.MatchesCompleted);
    }

    [Fact]
    public void Nothing_goes_below_zero_and_empty_results_have_no_ratios()
    {
        var total = new ServiceRecord { MatchesCompleted = 1, TimePlayed = "PT1M", CoreStats = new CoreStats { Kills = 1, ShotsFired = 10 } };
        var part = new ServiceRecord { MatchesCompleted = 3, TimePlayed = "PT5M", CoreStats = new CoreStats { Kills = 9, ShotsFired = 50 } };
        var r = ServiceRecordMath.Subtract(total, part);
        Assert.Equal(0, r.MatchesCompleted);
        Assert.Equal(0, r.CoreStats.Kills);
        Assert.Equal(0, r.CoreStats.Accuracy);
        Assert.Equal(0, r.CoreStats.AverageKDA);
        Assert.Equal("PT0S", r.TimePlayed);
    }

    [Fact]
    public void Time_played_and_medals_subtract()
    {
        var total = new ServiceRecord
        {
            TimePlayed = "P1DT2H",
            CoreStats = new CoreStats { Medals = [new(1, 10, 500), new(2, 3, 0), new(3, 1, 0)] },
        };
        var part = new ServiceRecord
        {
            TimePlayed = "PT3H",
            CoreStats = new CoreStats { Medals = [new(1, 4, 200), new(3, 1, 0)] },
        };
        var r = ServiceRecordMath.Subtract(total, part);
        Assert.Equal(TimeSpan.FromHours(23), IsoDuration.Parse(r.TimePlayed));
        Assert.Equal([new MedalCount(1, 6, 300), new MedalCount(2, 3, 0)], r.CoreStats.Medals);   // medal 3 is all gone
    }

    [Fact]
    public void Best_values_are_kept_and_durations_subtract_in_mode_groups()
    {
        JsonElement G(object o) => JsonSerializer.SerializeToElement(o);
        var total = new ServiceRecord
        {
            CoreStats = new CoreStats { MaxKillingSpree = 12 },
            ModeStats = new()
            {
                ["OddballStats"] = G(new { SkullGrabs = 10, TimeAsSkullCarrier = "PT5M", LongestTimeAsSkullCarrier = "PT1M", Ratio = 2.5, Name = "Skull" }),
                ["ZonesStats"] = G(new { ZoneCaptures = 4 }),
            },
        };
        var part = new ServiceRecord
        {
            CoreStats = new CoreStats { MaxKillingSpree = 20 },
            ModeStats = new() { ["OddballStats"] = G(new { SkullGrabs = 3, TimeAsSkullCarrier = "PT2M", LongestTimeAsSkullCarrier = "PT50S", Ratio = 1.0, Name = "x" }) },
        };
        var r = ServiceRecordMath.Subtract(total, part);
        var odd = r.ModeStats["OddballStats"];
        Assert.Equal(7, odd.GetProperty("SkullGrabs").GetInt64());
        Assert.Equal("PT3M", odd.GetProperty("TimeAsSkullCarrier").GetString());
        Assert.Equal("PT1M", odd.GetProperty("LongestTimeAsSkullCarrier").GetString());
        Assert.Equal(1.5, odd.GetProperty("Ratio").GetDouble());
        Assert.Equal("Skull", odd.GetProperty("Name").GetString());
        Assert.Equal(4, r.ModeStats["ZonesStats"].GetProperty("ZoneCaptures").GetInt32());   // not in part: unchanged
        Assert.Equal(12, r.CoreStats.MaxKillingSpree);
    }

    [Fact]
    public void Category_names_and_maximum_fields()
    {
        Assert.Equal("Oddball", GameVariantCategories.Name(18));
        Assert.Equal("Mode 99", GameVariantCategories.Name(99));
        Assert.DoesNotContain(GameVariantCategories.Firefight, GameVariantCategories.PvP);
        Assert.True(GameVariantCategories.IsMaximum("LongestTimeAsSkullCarrier"));
        Assert.True(GameVariantCategories.IsMaximum("MaxKillingSpree"));
        Assert.False(GameVariantCategories.IsMaximum("SkullGrabs"));
    }

    [Fact]
    public void Stats_filter_maps_to_the_ranked_parameter()
    {
        Assert.Null(new StatsFilter().IsRanked);
        Assert.True(new StatsFilter(Ranked: RankedFilter.Ranked).IsRanked);
        Assert.False(new StatsFilter(Ranked: RankedFilter.Unranked).IsRanked);
        Assert.Equal(StatsScope.PvP, new StatsFilter().Scope);
    }
}
