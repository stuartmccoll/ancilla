using System.Text;

namespace Ancilla.Theater.Format;

/// <summary>
/// A player found in the header stream. <see cref="Gamertag"/> is null when two matching copies
/// of the name weren't found in the player's record.
/// </summary>
public sealed record RosterEntry(ulong Xuid, long XuidBit, string? Gamertag, long? NameBit);

/// <summary>
/// Finds the roster in the header's bit stream without knowing the XUIDs in advance.
///
/// Observed (film v41, a Firefight and a PvP film): players appear in scoreboard order. Each
/// record starts with the XUID as a 64-bit MSB-first value, followed later by two identical
/// 16-character UTF-16LE gamertag fields (NUL-terminated), about 15,300 bits apart. The first copy is
/// about 12,300 bits after the XUID in most records, and about 900 in a shorter record variant.
/// </summary>
public static class Roster
{
    public const int NameChars = 16;
    public const int NameBytes = NameChars * 2;
    private const int MinCopyGap = 14_000, MaxCopyGap = 17_000;
    private const int MinNameChars = 3;   // shorter strings repeat by chance in zero-heavy data

    // Where the first name copy sits after the XUID, in bits: 936 in the short record layout, and
    // 11,896–12,342 in the long one (seen across 6 films). A name outside these windows belongs to
    // some other XUID-range value, not a player record.
    public static readonly IReadOnlyList<(int Min, int Max)> FirstCopyWindows = [(900, 1_000), (11_800, 12_450)];
    private static readonly int[] KnownFirstCopyOffsets = [936, 11_896, 12_267, 12_342];

    // Xbox Live XUIDs for real accounts are 0x0009_xxxx_xxxx_xxxx. An earlier, tighter upper bound
    // (2535999999999999) missed a real player whose XUID starts 2703…
    private const ulong XuidMin = 0x0009_0000_0000_0000UL;
    private const ulong XuidMax = 0x0009_FFFF_FFFF_FFFFUL;

    public static List<RosterEntry> Find(ReadOnlyMemory<byte> header, int streamOffset)
    {
        var span = header.Span;
        var candidates = new Dictionary<ulong, long>();
        ulong window = 0;
        var startBit = (long)streamOffset * 8;
        for (var bit = startBit; bit < (long)span.Length * 8; bit++)
        {
            window = (window << 1) | (uint)((span[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1);
            if (bit - startBit >= 63 && window is >= XuidMin and <= XuidMax) candidates.TryAdd(window, bit - 63);
        }

        // Other XUID-range values sit inside and between player records (see FORMAT.md) and can
        // find the same name. Keep the claimant whose distance best fits a known record layout.
        var bits = new ShiftedBits(span);
        var entries = candidates
            .Select(c => (c.Key, c.Value, name: FindName(bits, c.Value)))
            .Select(c => new RosterEntry(c.Key, c.Value, c.name?.Text, c.name?.Bit))
            .OrderBy(e => e.XuidBit)
            .ToList();
        var owners = entries.Where(e => e.NameBit is not null).GroupBy(e => e.NameBit)
            .Select(g => g.MinBy(e => KnownFirstCopyOffsets.Min(k => Math.Abs(e.NameBit!.Value - e.XuidBit - k)))!)
            .ToHashSet();
        return entries.Select(e => e.NameBit is null || owners.Contains(e) ? e : e with { Gamertag = null, NameBit = null }).ToList();
    }

    /// <summary>The 16-char UTF-16LE field <paramref name="gamertag"/> occupies in films.</summary>
    public static byte[] NameField(string gamertag)
    {
        var field = new byte[NameBytes];
        Encoding.Unicode.GetBytes(gamertag.AsSpan(0, Math.Min(gamertag.Length, NameChars)), field);
        return field;
    }

    // A name field in one of the first-copy windows with an identical copy 14,000–17,000 bits
    // later. At least one copy must be cleanly zero-padded, which keeps short chance strings out.
    private static (string Text, long Bit)? FindName(ShiftedBits bits, long xuidBit)
    {
        foreach (var (min, max) in FirstCopyWindows)
            for (var first = xuidBit + min; first <= xuidBit + max; first++)
            {
                if (ReadName(bits, first) is not { } a) continue;
                for (var second = first + MinCopyGap; second <= first + MaxCopyGap; second++)
                    if (ReadName(bits, second) is { } b && b.Text == a.Text && (a.Clean || b.Clean))
                        return (a.Text, first);
            }
        return null;
    }

    private static (string Text, bool Clean)? ReadName(ShiftedBits bits, long at)
    {
        var f = bits.At(at, NameBytes);
        if (f.Length < NameBytes || f[1] != 0 || !IsGamertagChar((char)f[0])) return null;   // cheap pre-check
        return DecodeName(f);
    }

    // The name ends at the first NUL. What follows is usually zeros, but the header's first copy can
    // hold stray bytes after the NUL (seen in two films), so it's ignored rather than rejected.
    /// <summary>
    /// Decodes a 32-byte name field: gamertag characters up to the first NUL, at least 3 of them.
    /// <c>Clean</c> is whether everything after the NUL is zero. Null when it isn't a name.
    /// </summary>
    public static (string Text, bool Clean)? DecodeName(ReadOnlySpan<byte> f)
    {
        var sb = new StringBuilder();
        var i = 0;
        for (; i < f.Length; i += 2)
        {
            var c = (char)(f[i] | f[i + 1] << 8);
            if (c == '\0') break;
            if (!IsGamertagChar(c)) return null;
            sb.Append(c);
        }
        if (sb.Length < MinNameChars) return null;
        var clean = true;
        for (; i < f.Length; i++) clean &= f[i] == 0;
        return (sb.ToString(), clean);
    }

    // Gamertags are letters, digits and spaces (modern ones may add a # suffix).
    public static bool IsGamertagChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' ' or '#';
}
