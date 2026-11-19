using Ancilla.Core;
using Ancilla.Testing;
using Ancilla.Web.Services;
using static Ancilla.Testing.Spartans;

namespace Ancilla.Web.Tests;

public class CompanionTests
{
    private static readonly HaloWorld World = new();
    private static IEnumerable<RecentMatch> Recent() => World.Games.Select(g => new RecentMatch(g.Entry, g.Stats));

    [Fact]
    public void Groups_every_other_human_by_xuid()
    {
        var all = Companion.FromMatches(Recent(), NobleSix.Xuid);
        Assert.DoesNotContain(NobleSix.Xuid, all.Keys);
        Assert.All(all.Keys, x => Assert.True(HaloIds.IsXuid(x)));          // no bots
        Assert.Equal(World.Games.Count(g => g.Stats.Players.Any(p => p.Xuid == Kat.Xuid)), all[Kat.Xuid].Games);
    }

    [Fact]
    public void Teammates_and_opponents_are_counted_apart()
    {
        var all = Companion.FromMatches(Recent(), NobleSix.Xuid);
        var carter = all[Carter.Xuid];   // swaps sides every other team game
        Assert.True(carter.WithGames > 0);
        Assert.True(carter.AgainstGames > 0);
        Assert.Equal(carter.Games, carter.WithGames + carter.AgainstGames);
        Assert.All(carter.Matches.Where(m => m.Teammates), m => Assert.Equal(m.Me.LastTeamId, m.Them.LastTeamId));

        var thel = all[Thel.Xuid];   // always the other side, or free-for-all
        Assert.Equal(0, thel.WithGames);
        Assert.Equal(thel.Games, thel.AgainstGames);
    }

    [Fact]
    public void Free_for_all_counts_as_opponents()
    {
        var ffa = World.Games.First(g => !g.Stats.MatchInfo.TeamsEnabled);
        var all = Companion.FromMatches([new RecentMatch(ffa.Entry, ffa.Stats)], NobleSix.Xuid);
        Assert.All(all.Values, c => Assert.Equal(1, c.AgainstGames));
    }

    [Fact]
    public void Matches_without_me_are_ignored()
    {
        var game = World.Games[0];
        var withoutMe = game.Stats with { Players = game.Stats.Players.Where(p => p.Xuid != NobleSix.Xuid).ToList() };
        Assert.Empty(Companion.FromMatches([new RecentMatch(game.Entry, withoutMe)], NobleSix.Xuid));
    }

    [Fact]
    public void Kd_adds_up_kills_and_deaths_first()
    {
        Assert.Equal(1.5, Companion.Kd([new CoreStats { Kills = 10, Deaths = 2 }, new CoreStats { Kills = 5, Deaths = 8 }]));
        Assert.Equal(7, Companion.Kd([new CoreStats { Kills = 7 }, null]));
        Assert.Equal(0, Companion.Kd([]));
    }

    [Fact]
    public void Firefight_is_pve_and_slayer_is_not()
    {
        var ff = World.Games.First(g => g.IsPve);
        var slayer = World.Games.First(g => !g.IsPve);
        Assert.True(new RecentMatch(ff.Entry, ff.Stats).IsPve(NobleSix.Xuid));
        Assert.False(new RecentMatch(slayer.Entry, slayer.Stats).IsPve(NobleSix.Xuid));
        Assert.False(new RecentMatch(ff.Entry, ff.Stats).IsPve(Thel.Xuid));   // not in that match
    }
}
