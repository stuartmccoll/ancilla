using Ancilla.Core;

namespace Ancilla.Web.Services;

/// <summary>One match you shared with another player.</summary>
public sealed record SharedMatch(RecentMatch Match, MatchPlayer Me, MatchPlayer Them)
{
    /// <summary>Same team in a team game. Free-for-all counts as opponents.</summary>
    public bool Teammates => Match.Stats.MatchInfo.TeamsEnabled && Me.LastTeamId == Them.LastTeamId;
    public bool Won => Me.Outcome == MatchOutcome.Win;
    public CoreStats? MyStats => Me.Stats?.CoreStats;
    public CoreStats? TheirStats => Them.Stats?.CoreStats;
}

/// <summary>Everything about one other player across the matches you've both been in.</summary>
public sealed class Companion(string xuid)
{
    public string Xuid { get; } = xuid;
    public List<SharedMatch> Matches { get; } = [];

    public int Games => Matches.Count;
    public int WithGames => Matches.Count(m => m.Teammates);
    public int WithWins => Matches.Count(m => m.Teammates && m.Won);
    public int AgainstGames => Matches.Count(m => !m.Teammates);
    public int AgainstWins => Matches.Count(m => !m.Teammates && m.Won);
    public DateTimeOffset LastPlayed => Matches.Max(m => m.Match.Entry.MatchInfo.StartTime);

    public double TheirKd => Kd(Matches.Select(m => m.TheirStats));
    public double MyKd => Kd(Matches.Select(m => m.MyStats));

    public static double Kd(IEnumerable<CoreStats?> stats)
    {
        var list = stats.OfType<CoreStats>().ToList();
        var d = list.Sum(s => s.Deaths);
        var k = list.Sum(s => s.Kills);
        return d == 0 ? k : (double)k / d;
    }

    /// <summary>Groups every other human player across <paramref name="matches"/> by xuid.</summary>
    public static Dictionary<string, Companion> FromMatches(IEnumerable<RecentMatch> matches, string myXuid)
    {
        var all = new Dictionary<string, Companion>();
        foreach (var m in matches)
        {
            var me = m.Stats.Players.FirstOrDefault(p => p.Xuid == myXuid);
            if (me is null) continue;
            foreach (var p in m.Stats.Players)
            {
                if (p.Xuid is not { } x || x == myXuid || !p.IsHuman) continue;
                if (!all.TryGetValue(x, out var c)) all[x] = c = new Companion(x);
                c.Matches.Add(new SharedMatch(m, me, p));
            }
        }
        return all;
    }
}
