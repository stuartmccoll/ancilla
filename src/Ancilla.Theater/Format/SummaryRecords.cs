
namespace Ancilla.Theater.Format;

/// <summary>One summary record, found by walking the payload rather than by searching for a known name.</summary>
public sealed record SummaryRecord(long StartBit, long NameBit, string Gamertag, int Code, uint TimeMs, bool IsMedal, int MedalCode);

/// <summary>
/// Walks the summary's type-9 payload record by record, with no roster needed. Checked on six v41
/// films (it reproduces the name-based decode exactly, and also finds late joiners' records):
/// <code>
/// u32                 record count
/// then, per record:
///   body              the player's header record from just after its XUID, unchanged all match
///                     (header XUID-to-name offset + 15,030 bits long, so ~16,000 or ~27,400)
///   char16[16]        gamertag, NUL-terminated
///   224 bits          event data: code +376, time +384, medal flag +447, medal code +472
///                     (offsets from the name); every other bit here is constant
///   u32               always 12000 (meaning unknown)
/// then 0–6 bits of padding
/// </code>
/// Only the event bits change from one record to the next, so kill records hold no position or
/// weapon: those have to come from the replication stream.
/// </summary>
public static class SummaryRecords
{
    public const int CountBits = 32, SeparatorBits = 32;
    public const int BodyAfterHeaderOffset = 15_030;   // body length = header XUID-to-name offset + this
    public const int NameAndEventBits = 480;           // 256-bit name field + 224 event bits
    public const uint Separator = 12_000;

    /// <summary>The records in order, the count stored at the start, and whether every separator was 12000.</summary>
    public static (List<SummaryRecord> Records, uint StoredCount, bool SeparatorsOk) Walk(ReadOnlyMemory<byte> payload)
    {
        var r = new BitReader(payload);
        var storedCount = (uint)r.Read(CountBits);
        var records = new List<SummaryRecord>();
        var separatorsOk = true;
        var start = (long)CountBits;
        while (start + NameAndEventBits + SeparatorBits <= r.Length && FindName(r, start) is { } found)
        {
            var at = found.Bit;
            // The whole record (name, event bits, separator) must fit; a truncated payload ends the
            // walk, and the stored count then shows the shortfall.
            if (at + NameAndEventBits + SeparatorBits > r.Length) break;
            ulong Read(int offset, int n) { r.Position = at + offset; return r.Read(n); }
            var medal = Read(Summary.MedalFlagOffset, 1) == 1;
            records.Add(new SummaryRecord(start, at, found.Name,
                (int)Read(Summary.CodeOffset, Summary.CodeBits), (uint)Read(Summary.TimeOffset, Summary.TimeBits),
                medal, medal ? (int)Read(Summary.MedalCodeOffset, Summary.MedalCodeBits) : -1));
            separatorsOk &= Read(NameAndEventBits, SeparatorBits) == Separator;
            start = at + NameAndEventBits + SeparatorBits;
        }
        return (records, storedCount, separatorsOk);
    }

    // The gamertag field sits at body length past the record start. The body is the player's header
    // record, so its length follows the header's XUID-to-name offset windows (Roster.FirstCopyWindows).
    private static (long Bit, string Name)? FindName(BitReader r, long start)
    {
        foreach (var (min, max) in Roster.FirstCopyWindows)
            for (var off = min; off <= max; off++)
            {
                var bit = start + off + BodyAfterHeaderOffset;
                if (bit + 256 > r.Length) return null;
                r.Position = bit;
                var first = (char)r.Read(8);
                if (r.Read(8) != 0 || !Roster.IsGamertagChar(first)) continue;   // cheap pre-check
                r.Position = bit;
                // Summary copies of the name are always cleanly zero-padded.
                if (Roster.DecodeName(r.ReadBytes(Roster.NameBytes)) is { Clean: true } name) return (bit, name.Text);
            }
        return null;
    }
}
