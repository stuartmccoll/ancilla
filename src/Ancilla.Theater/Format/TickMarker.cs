namespace Ancilla.Theater.Format;

/// <summary>
/// The type-10 packet that starts each 60 Hz tick (a type-0 packet always follows it). MSB-first:
/// <code>
/// 32 slots: [present:1], then if present [value:6]
/// zero padding; payload length = ceil(bits used / 8)
/// </code>
/// Present slots follow the humans in the match (bots never appear). Values read 60 at the start and
/// on what look like quitters' slots, and are otherwise small and steady: perhaps latency in ticks.
/// Checked on eleven v41 films: all but 5 of 315,632 packets fit.
/// </summary>
public static class TickMarker
{
    public const int Slots = 32, ValueBits = 6;

    /// <summary>
    /// Each slot's value (null when absent), and whether the payload fits the layout exactly:
    /// the right length, with only zero bits after the last slot.
    /// </summary>
    public static (int?[] Values, bool LayoutOk) Parse(ReadOnlyMemory<byte> payload)
    {
        var values = new int?[Slots];
        var r = new BitReader(payload);
        for (var s = 0; s < Slots; s++)
        {
            if (r.Position + 1 > r.Length) return (values, false);
            if (r.Read(1) == 0) continue;
            if (r.Position + ValueBits > r.Length) return (values, false);
            values[s] = (int)r.Read(ValueBits);
        }
        var used = r.Position;
        if ((used + 7) / 8 != payload.Length) return (values, false);
        while (r.Position < r.Length) if (r.Read(1) != 0) return (values, false);
        return (values, true);
    }
}
