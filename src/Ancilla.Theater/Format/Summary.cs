namespace Ancilla.Theater.Format;

/// <summary>
/// What a summary record is. <see cref="Objective"/> is a mode-objective event: in CTF, one per flag
/// steal, capture, capture assist, return and carrier kill, plus some pickups; in Firefight, a
/// 5-second award for holding an objective.
/// </summary>
public enum SummaryEventKind { Other, PlayerKill, Death, Medal, Objective }

/// <summary>
/// One record from the summary (type-3) chunk: something that happened to <see cref="Player"/>
/// (an index into the roster) at <see cref="TimeMs"/> into the match. <see cref="TypeByte"/> is the
/// byte at +472: the medal code for medals, otherwise the record type.
/// </summary>
public sealed record SummaryEvent(
    int Player, long NameBit, uint TimeMs, int Code, bool IsMedal, int MedalCode, SummaryEventKind Kind, int TypeByte = 0)
{
    /// <summary>For a kill: the roster index of the player killed. Set by <see cref="Summary.PairKills"/>.</summary>
    public int? Victim { get; set; }
}

/// <summary>
/// Decodes the summary chunk's type-9 packet: a record count, then per-player records, bit-packed
/// MSB-first, in time order (checked on six v41 films; see <see cref="SummaryRecords"/> for the
/// layout). Each record holds the player's 16-character gamertag field, and the event fields sit
/// at fixed offsets from it:
/// <code>
/// +376  7 bits   event code: 10 = death, 25 = killed a player (AI kills aren't recorded),
///                5 = objective; for medals, 25/50/75 by medal difficulty (normal/heroic/legendary)
/// +384  32 bits  time into the match, milliseconds (big-endian, like every field here)
/// +447  1 bit    medal flag
/// +472  8 bits   medals: medal code (the game's own numbering; see MedalCodes);
///                otherwise the record type: 0 = kill, 1 = death, 2 = objective
/// </code>
/// A kill is always followed by the victim's death record, 0–1 ms later.
/// </summary>
public static class Summary
{
    public const int CodeOffset = 376, CodeBits = 7;
    // First read as 20 bits at +396, which tops out at 17:28.6. The 12 bits above it are zero in
    // every record of six films, and the whole u32 is the time (it's needed past 17.5 minutes).
    public const int TimeOffset = 384, TimeBits = 32;
    public const int MedalFlagOffset = 447;
    // Read as 7 bits at +473 until codes of 128 and above turned up (151 medals need 8 bits).
    public const int MedalCodeOffset = 472, MedalCodeBits = 8;

    /// <summary>The summary chunk's type-9 packet payload, which holds every record.</summary>
    public static ReadOnlyMemory<byte> Payload(IEnumerable<Packet> summaryPackets) =>
        summaryPackets.FirstOrDefault(p => p.Type == 9) is { Payload: var payload, Type: 9 }
            ? payload
            : throw new InvalidDataException("The summary chunk has no type-9 packet.");

    public const int DeathCode = 10;
    public const int PlayerKillCode = 25;
    public const int ObjectiveCode = 5;

    /// <summary>
    /// The +472 record type that goes with a non-medal event code (checked on eleven films: no other
    /// pairing occurs), or null for a code not seen yet.
    /// </summary>
    public static int? RecordTypeOf(int code) => code switch
    {
        PlayerKillCode => 0,
        DeathCode => 1,
        ObjectiveCode => 2,
        _ => null,
    };

    /// <summary>
    /// Decodes events for the players in <paramref name="gamertags"/>, by walking the records
    /// (<see cref="SummaryRecords.Walk"/>). An event's player is its index in the list. Records for
    /// gamertags not in the list are skipped.
    /// </summary>
    public static List<SummaryEvent> Decode(ReadOnlyMemory<byte> payload, IReadOnlyList<string?> gamertags) =>
        FromRecords(SummaryRecords.Walk(payload).Records, gamertags);

    /// <summary>As <see cref="Decode"/>, for records already walked.</summary>
    public static List<SummaryEvent> FromRecords(IEnumerable<SummaryRecord> records, IReadOnlyList<string?> gamertags)
    {
        var index = new Dictionary<string, int>();
        for (var p = gamertags.Count - 1; p >= 0; p--)
            if (gamertags[p] is { } g) index[g] = p;
        return records
            .Where(r => index.ContainsKey(r.Gamertag))
            .Select(r => new SummaryEvent(index[r.Gamertag], r.NameBit, r.TimeMs, r.Code, r.IsMedal, r.MedalCode, KindOf(r.IsMedal, r.Code), r.TypeByte))
            .ToList();
    }

    private static SummaryEventKind KindOf(bool medal, int code) =>
        medal ? SummaryEventKind.Medal
        : code == DeathCode ? SummaryEventKind.Death
        : code == PlayerKillCode ? SummaryEventKind.PlayerKill
        : code == ObjectiveCode ? SummaryEventKind.Objective
        : SummaryEventKind.Other;

    /// <summary>
    /// The original decoder: finds each known gamertag at every bit offset and reads the fields
    /// around it. Kept only so <c>inspect</c> can check that walking by layout gives the same records.
    /// </summary>
    public static List<SummaryEvent> DecodeByNameSearch(ReadOnlyMemory<byte> payload, IReadOnlyList<string?> gamertags)
    {
        var bits = new ShiftedBits(payload.Span);
        var events = new List<SummaryEvent>();
        for (var p = 0; p < gamertags.Count; p++)
        {
            if (gamertags[p] is not { } name) continue;
            foreach (var at in bits.FindAll(Roster.NameField(name)))
            {
                if ((at + MedalCodeOffset + MedalCodeBits) > (long)payload.Length * 8) continue;
                var r = new BitReader(payload);
                ulong Read(int offset, int n) { r.Position = at + offset; return r.Read(n); }
                var code = (int)Read(CodeOffset, CodeBits);
                var time = (uint)Read(TimeOffset, TimeBits);
                var medal = Read(MedalFlagOffset, 1) == 1;
                var typeByte = (int)Read(MedalCodeOffset, MedalCodeBits);
                events.Add(new SummaryEvent(p, at, time, code, medal, medal ? typeByte : -1, KindOf(medal, code), typeByte));
            }
        }
        events.Sort((a, b) => a.NameBit.CompareTo(b.NameBit));
        return events;
    }

    /// <summary>
    /// Links each kill to the death record that follows it. Returns how many kills found a victim.
    /// </summary>
    public static int PairKills(List<SummaryEvent> events)
    {
        var paired = 0;
        for (var i = 0; i + 1 < events.Count; i++)
        {
            var (k, d) = (events[i], events[i + 1]);
            if (k.Kind != SummaryEventKind.PlayerKill || d.Kind != SummaryEventKind.Death || d.Player == k.Player) continue;
            if (d.TimeMs - k.TimeMs > 2) continue;
            k.Victim = d.Player;
            paired++;
        }
        return paired;
    }
}
