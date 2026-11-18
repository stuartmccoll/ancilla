namespace Ancilla.Theater.Format;

/// <summary>
/// One alive span of one player, in type-0 packet indexes (inclusive). <see cref="EndsInDeath"/> when
/// one of the player's deaths ended it, rather than the player leaving or the film ending.
/// </summary>
public readonly record struct AliveSpan(int Player, int FirstTick, int LastTick, bool EndsInDeath = false)
{
    public bool Contains(int tick) => tick >= FirstTick && tick <= LastTick;
}

/// <summary>
/// The per-player list at the end of every type-0 packet: one entry per player who is alive (and
/// during the intro, before play starts), so a player's entry is missing exactly while they're dead
/// (FORMAT.md, "Per-player list"). MSB-first, index ascending, then a <c>0</c> bit and fewer than 8
/// zero bits of padding:
/// <code>
/// [1] [index:8] [status:21]   status starts 11; its other bits aren't decoded
///               [5 bits]      when status bit 18 (counting from 0, MSB first) is 1
///                 [16 bits]   when the last of those 5 bits is 1
///               [16 bits]     when status bit 19 is 1
/// </code>
/// The list's start isn't marked, so <see cref="Parse"/> takes the earliest start whose entries read
/// exactly to the padding. Entries whose status doesn't start <c>11</c> stop a parse: that rule is
/// what separates the list from the records before it. Indexes follow the scoreboard order among
/// humans in most films, but not all (late joiners shift them or take over a quitter's), so
/// <see cref="Spans"/> learns which player each life belongs to from the summary's deaths.
/// </summary>
public static class PlayerList
{
    /// <summary>Highest index accepted. No film has had more than 9 humans; higher values are chance parses.</summary>
    public const int MaxIndex = 31;
    /// <summary>Presence changes shorter than this many ticks (1 s) are parse noise, not lives or deaths.</summary>
    public const int MinRunTicks = 60;
    /// <summary>How close (ticks) a span's end must be to a death for the death to claim it.</summary>
    public const int DeathWindowTicks = 30;

    private const int EntryBits = 1 + 8 + 21;

    /// <summary>The indexes in a type-0 payload's list, ascending, or null when no reading fits.</summary>
    public static int[]? Parse(ReadOnlySpan<byte> payload)
    {
        var n = payload.Length * 8;
        var last = n - 1;
        while (last >= 0 && Bit(payload, last) == 0) last--;
        if (last < 0 || last == n - 1) return null;   // no terminating 0
        // The list ends on its terminating 0: after the last 1 bit, with fewer than 8 bits after it.
        var minEnd = Math.Max(last + 1, n - 8);

        var indexes = new List<int>();
        for (var s = 0; s <= last; s++)
        {
            if (Bit(payload, s) != 1) continue;
            indexes.Clear();
            int pos = s, prev = -1;
            var ok = true;
            while (pos < n && Bit(payload, pos) == 1)
            {
                if (pos + EntryBits > n) { ok = false; break; }
                var index = Read(payload, pos + 1, 8);
                var status = Read(payload, pos + 9, 21);
                if (index <= prev || index > MaxIndex || status >> 19 != 0b11) { ok = false; break; }
                indexes.Add(index);
                prev = index;
                var length = EntryBits;
                if ((status >> 2 & 1) == 1)
                {
                    // The 5-bit extension's last bit says whether 16 more bits follow.
                    if (pos + EntryBits + 5 > n) { ok = false; break; }
                    length += 5 + (Bit(payload, pos + EntryBits + 4) == 1 ? 16 : 0);
                }
                if ((status >> 1 & 1) == 1) length += 16;
                pos += length;
            }
            if (ok && pos >= minEnd && pos < n) return [.. indexes];
        }
        return null;
    }

    private static int Bit(ReadOnlySpan<byte> b, int i) => (b[i >> 3] >> (7 - (i & 7))) & 1;

    private static int Read(ReadOnlySpan<byte> b, int at, int bits)
    {
        var v = 0;
        for (var i = 0; i < bits; i++) v = (v << 1) | Bit(b, at + i);
        return v;
    }

    /// <summary>
    /// Per index, whether it's listed at each tick. A packet that doesn't parse keeps the previous
    /// packet's list; runs and gaps shorter than <see cref="MinRunTicks"/> are removed.
    /// </summary>
    public static Dictionary<int, bool[]> Presence(IReadOnlyList<int[]?> lists)
    {
        var n = lists.Count;
        var present = new Dictionary<int, bool[]>();
        int[] previous = [];
        for (var t = 0; t < n; t++)
        {
            var now = lists[t] ?? previous;
            foreach (var i in now) (present.TryGetValue(i, out var a) ? a : present[i] = new bool[n])[t] = true;
            previous = now;
        }
        foreach (var a in present.Values) { Smooth(a, value: true); Smooth(a, value: false); }
        return present.Where(x => x.Value.Contains(true)).ToDictionary();
    }

    /// <summary>
    /// Each player's alive spans, from <see cref="Presence"/> and the summary's deaths (tick and
    /// player, in time order). A span that ends within <see cref="DeathWindowTicks"/> of a death is
    /// that player's (each death claims one span, nearest first). A span with no death at its end (a
    /// life still going at the end, or a quitter's last) belongs to whoever owns the last claimed
    /// span before it on the same index, or the first one after it when there's none before. Spans on
    /// an index no death ever claims aren't assigned.
    /// </summary>
    public static List<AliveSpan> Spans(IReadOnlyDictionary<int, bool[]> presence, IReadOnlyList<(int Tick, int Player)> deaths)
    {
        var runs = presence.SelectMany(x => Runs(x.Value).Select(r => (Index: x.Key, r.First, r.Last))).ToList();
        var owner = new int?[runs.Count];
        var pairs = new List<(int Run, int Death, int Distance)>();
        for (var r = 0; r < runs.Count; r++)
            for (var d = 0; d < deaths.Count; d++)
                if (Math.Abs(deaths[d].Tick - runs[r].Last) <= DeathWindowTicks) pairs.Add((r, d, Math.Abs(deaths[d].Tick - runs[r].Last)));
        var usedDeaths = new HashSet<int>();
        foreach (var (r, d, _) in pairs.OrderBy(p => p.Distance).ThenBy(p => p.Death))
            if (owner[r] is null && usedDeaths.Add(d)) owner[r] = deaths[d].Player;

        var spans = new List<AliveSpan>();
        for (var r = 0; r < runs.Count; r++)
        {
            var claimed = Enumerable.Range(0, runs.Count).Where(o => owner[o] is not null && runs[o].Index == runs[r].Index).ToList();
            var player = owner[r]
                ?? claimed.Where(o => runs[o].Last < runs[r].First).OrderByDescending(o => runs[o].Last).Select(o => owner[o]).FirstOrDefault()
                ?? claimed.Where(o => runs[o].First > runs[r].Last).OrderBy(o => runs[o].First).Select(o => owner[o]).FirstOrDefault();
            if (player is { } p) spans.Add(new AliveSpan(p, runs[r].First, runs[r].Last, owner[r] is not null));
        }
        return spans.OrderBy(s => s.FirstTick).ThenBy(s => s.Player).ToList();
    }

    /// <summary>Runs of true, as inclusive (first, last) ticks.</summary>
    public static List<(int First, int Last)> Runs(bool[] a)
    {
        var runs = new List<(int, int)>();
        for (var i = 0; i < a.Length;)
        {
            if (!a[i]) { i++; continue; }
            var j = i;
            while (j < a.Length && a[j]) j++;
            runs.Add((i, j - 1));
            i = j;
        }
        return runs;
    }

    // Flips runs of `value` shorter than MinRunTicks. Gaps at either end are left alone (not yet joined, or gone).
    private static void Smooth(bool[] a, bool value)
    {
        for (var i = 0; i < a.Length;)
        {
            if (a[i] != value) { i++; continue; }
            var j = i;
            while (j < a.Length && a[j] == value) j++;
            var atEdge = i == 0 || j == a.Length;
            if (j - i < MinRunTicks && (value || !atEdge))
                for (var k = i; k < j; k++) a[k] = !value;
            i = j;
        }
    }
}
