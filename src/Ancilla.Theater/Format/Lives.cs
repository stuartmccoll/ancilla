namespace Ancilla.Theater.Format;

/// <summary>One biped slot's positions in tick order: one life of one player (or AI).</summary>
public sealed record BipedTrack(int Slot, List<(int Tick, FilmPosition Position)> Points)
{
    public int FirstTick => Points[0].Tick;
    public int LastTick => Points[^1].Tick;
}

/// <summary>
/// Turns biped position records into per-player lives, using the summary's deaths. A death ends the
/// victim's biped track within a few ticks (<see cref="MatchDeaths"/>), and the victim's next life
/// is a new biped slot about 8 s later in CTF (<see cref="Chain"/>). Lives that neither end in a death
/// nor follow one (a player with no deaths) aren't assigned by the chaining; the per-player list fills
/// in some (<see cref="FillFromSpans"/>).
/// </summary>
public static class Lives
{
    public static Dictionary<int, BipedTrack> Tracks(IEnumerable<(int Tick, EntityPosition Record)> bipeds) =>
        bipeds.GroupBy(r => r.Record.Slot).ToDictionary(g => g.Key, g => new BipedTrack(g.Key, g.Select(r => (r.Tick, r.Record.Position)).ToList()));

    /// <summary>
    /// For each death (by index), the slot whose track ends nearest its tick, within
    /// <paramref name="window"/> ticks, each slot used once. <paramref name="shift"/> moves every death
    /// (for a baseline).
    /// </summary>
    public static Dictionary<int, int> MatchDeaths(IReadOnlyList<int> deathTicks, IReadOnlyDictionary<int, BipedTrack> tracks, int shift = 0, int window = 3)
    {
        var used = new HashSet<int>(); var got = new Dictionary<int, int>();
        for (var n = 0; n < deathTicks.Count; n++)
        {
            var dk = deathTicks[n] + shift;
            var best = tracks.Values.Where(t => !used.Contains(t.Slot) && Math.Abs(t.LastTick - dk) <= window)
                .OrderBy(t => Math.Abs(t.LastTick - dk)).ThenBy(t => t.Slot).Select(t => (int?)t.Slot).FirstOrDefault();
            if (best is { } s) { used.Add(s); got[n] = s; }
        }
        return got;
    }

    /// <summary>
    /// For each matched death (by index), the new slot first seen <paramref name="minMs"/>–<paramref name="maxMs"/>
    /// after it: the victim's respawn. Deaths are taken in time order, each slot used once, the one
    /// closest to <paramref name="expectedMs"/> (the CTF respawn time) first.
    /// </summary>
    public static Dictionary<int, int> Respawns(IReadOnlyList<double> deathMs, IReadOnlyCollection<int> matchedDeaths,
        IReadOnlyDictionary<int, BipedTrack> tracks, IReadOnlyList<double> tickMs, double minMs = 7_500, double maxMs = 9_500, double expectedMs = 8_050)
    {
        var used = new HashSet<int>(); var got = new Dictionary<int, int>();
        foreach (var n in matchedDeaths.OrderBy(n => deathMs[n]))
        {
            var s = tracks.Values.Where(t => !used.Contains(t.Slot) && tickMs[t.FirstTick] - deathMs[n] is var gap && gap >= minMs && gap <= maxMs)
                .OrderBy(t => Math.Abs(tickMs[t.FirstTick] - deathMs[n] - expectedMs)).Select(t => (int?)t.Slot).FirstOrDefault();
            if (s is { } slot) { used.Add(slot); got[n] = slot; }
        }
        return got;
    }

    /// <summary>
    /// Each player's lives (slots) in order. A life that ends in a death is the slot that death ends;
    /// the respawn pairing is used only for the life after a player's last death, or when their next
    /// death wasn't matched to a slot. Pairings disagree with the next death's slot in up to 15% of
    /// cases (several players respawning together), so deaths are trusted first.
    /// <paramref name="deathPlayers"/> gives each death's player, in time order.
    /// </summary>
    public static Dictionary<int, List<int>> Chain(IReadOnlyList<int> deathPlayers, IReadOnlyDictionary<int, int> matched,
        IReadOnlyDictionary<int, int> respawns, IReadOnlyDictionary<int, BipedTrack> tracks)
    {
        var lives = new Dictionary<int, List<int>>();
        void Add(int player, int slot) { var l = lives.TryGetValue(player, out var x) ? x : lives[player] = []; if (!l.Contains(slot)) l.Add(slot); }
        var claimed = matched.Values.ToHashSet();
        for (var n = 0; n < deathPlayers.Count; n++)
        {
            var p = deathPlayers[n];
            if (matched.TryGetValue(n, out var ended)) Add(p, ended);
            var next = Enumerable.Range(n + 1, deathPlayers.Count - n - 1).FirstOrDefault(m => deathPlayers[m] == p, -1);
            if ((next < 0 || !matched.ContainsKey(next)) && respawns.TryGetValue(n, out var slot) && !claimed.Contains(slot)) Add(p, slot);
        }
        foreach (var l in lives.Values) l.Sort((a, b) => tracks[a].FirstTick.CompareTo(tracks[b].FirstTick));
        return lives;
    }

    /// <summary>
    /// Adds lives the chaining missed, from the per-player list's alive spans (<see cref="PlayerList.Spans"/>):
    /// for a span none of the player's lives overlaps, the one unclaimed track that starts within
    /// <paramref name="windowTicks"/> of the span's start and ends before the span does (plus the
    /// window). Spans with no such track, or more than one, are left alone. Most missing lives have no
    /// biped records at all, so this closes only a few (5 of 36 across 15 films, 2026-11-18). Returns
    /// how many it added; <paramref name="lives"/> is changed in place.
    /// </summary>
    public static int FillFromSpans(Dictionary<int, List<int>> lives, IReadOnlyList<AliveSpan> spans,
        IReadOnlyDictionary<int, BipedTrack> tracks, int windowTicks = 60)
    {
        var claimed = lives.Values.SelectMany(x => x).ToHashSet();
        var added = 0;
        foreach (var s in spans)
        {
            var mine = lives.GetValueOrDefault(s.Player) ?? [];
            if (mine.Any(slot => tracks[slot].FirstTick <= s.LastTick && tracks[slot].LastTick >= s.FirstTick)) continue;
            var candidates = tracks.Values.Where(t => !claimed.Contains(t.Slot)
                && Math.Abs(t.FirstTick - s.FirstTick) <= windowTicks && t.LastTick <= s.LastTick + windowTicks).Take(2).ToList();
            if (candidates.Count != 1) continue;
            var list = lives.TryGetValue(s.Player, out var l) ? l : lives[s.Player] = [];
            list.Add(candidates[0].Slot);
            list.Sort((a, b) => tracks[a].FirstTick.CompareTo(tracks[b].FirstTick));
            claimed.Add(candidates[0].Slot);
            added++;
        }
        return added;
    }
}
