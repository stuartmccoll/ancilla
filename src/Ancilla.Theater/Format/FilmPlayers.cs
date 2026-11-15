using Ancilla.Core;

namespace Ancilla.Theater.Format;

/// <summary>A player whose summary records can be decoded, with their scoreboard row.</summary>
public sealed record FilmPlayer(string Gamertag, MatchPlayer Row, bool LateJoiner);

/// <summary>
/// Works out whose summary records are whose. Players in the header roster are matched to the
/// scoreboard by XUID. Players who joined after the header was written have summary records but
/// no XUID anywhere in the film, so each of those gamertags is linked to a scoreboard player who
/// joined in progress by comparing kills, deaths and medals. A link is made only when exactly one
/// such player fits, and no other gamertag fits that player.
/// </summary>
public static class FilmPlayers
{
    /// <summary>
    /// Roster players on the scoreboard (header order), then linked late joiners (order of first
    /// record). <c>Unlinked</c> lists gamertags in the summary that couldn't be linked.
    /// </summary>
    public static (List<FilmPlayer> Players, List<string> Unlinked) Resolve(
        IReadOnlyList<RosterEntry> named, IReadOnlyList<SummaryRecord> records, MatchStats board)
    {
        var players = named
            .Select(r => (r.Gamertag, Row: board.Players.FirstOrDefault(p => p.Xuid == r.Xuid.ToString())))
            .Where(x => x.Gamertag is not null && x.Row is not null)
            .Select(x => new FilmPlayer(x.Gamertag!, x.Row!, LateJoiner: false))
            .ToList();

        var known = named.Select(r => r.Gamertag).OfType<string>().ToHashSet();
        var others = records.Select(r => r.Gamertag).Where(g => !known.Contains(g)).Distinct().ToList();
        var candidates = board.Players
            .Where(p => p.IsHuman && p.ParticipationInfo?.JoinedInProgress == true && players.All(x => x.Row != p))
            .ToList();

        (int, int, int) Counts(string gamertag)
        {
            var mine = records.Where(r => r.Gamertag == gamertag).ToList();
            return (mine.Count(r => !r.IsMedal && r.Code == Summary.PlayerKillCode),
                    mine.Count(r => !r.IsMedal && r.Code == Summary.DeathCode),
                    mine.Count(r => r.IsMedal));
        }
        var fits = others.ToDictionary(g => g, g => candidates.Where(c => Stats(c) == Counts(g)).ToList());

        var unlinked = new List<string>();
        foreach (var g in others)
        {
            if (fits[g] is [var row] && others.Count(o => fits[o].Contains(row)) == 1)
                players.Add(new FilmPlayer(g, row, LateJoiner: true));
            else
                unlinked.Add(g);
        }
        return (players, unlinked);
    }

    /// <summary>The scoreboard's player kills (PvP kills where present), deaths and medal count.</summary>
    public static (int Kills, int Deaths, int Medals) Stats(MatchPlayer p)
    {
        var core = p.Stats?.CoreStats;
        var kills = p.Stats?.ModeStats.TryGetValue("PvpStats", out var pvp) == true && pvp.TryGetProperty("Kills", out var k)
            ? k.GetInt32()
            : core?.Kills ?? 0;
        return (kills, core?.Deaths ?? 0, core?.Medals.Sum(x => x.Count) ?? 0);
    }
}
