using Ancilla.Theater.Format;

namespace Ancilla.Theater;

/// <summary>
/// Biped and dropped-object positions from a cached film's type-0 packets, with each tick's time on
/// the summary's event clock (packet time from the film's first packet; see FORMAT.md). Aims are
/// from biped records whose command tick checked, for the biped slots kept in <see cref="Bipeds"/>.
/// </summary>
public sealed record FilmPositions(
    List<double> TickMs, List<(int Tick, EntityPosition Record)> Bipeds, List<(int Tick, EntityPosition Record)> Objects,
    List<(int Tick, int Slot, Aim Aim)> Aims)
{
    public static FilmPositions Load(FilmCache cache, string matchId)
    {
        var m = cache.LoadManifest(matchId);
        var ticks = new List<double>();
        var bipeds = new List<(int, EntityPosition)>();
        var objects = new List<(int, EntityPosition)>();
        var aims = new List<(int Tick, int Slot, Aim Aim)>();
        ulong? firstUs = null;
        foreach (var c in m.CustomData.Chunks.Where(c => c.ChunkType == 2).OrderBy(c => c.Index))
            foreach (var p in PacketReader.Read(cache.LoadChunk(matchId, c), out _))
            {
                firstUs ??= p.TimestampUs;
                if (p.Type != 0) continue;
                var (b, o) = EntityRecords.Find(p.Payload.Span);
                bipeds.AddRange(b.Select(r => (ticks.Count, r)));
                objects.AddRange(o.Select(r => (ticks.Count, r)));
                foreach (var u in EntityRecords.FindBipedUpdates(p.Payload.Span))
                    if (u.Checked && u.Aim is { } a) aims.Add((ticks.Count, u.Slot, a));
                ticks.Add((p.TimestampUs - firstUs.Value) / 1000.0);
            }
        var kept = EntityRecords.FilterBipeds(bipeds, objects);
        var slots = kept.Select(r => r.Record.Slot).ToHashSet();
        return new FilmPositions(ticks, kept, objects, aims.Where(a => slots.Contains(a.Slot)).ToList());
    }

    /// <summary>The last tick at or before a time on the event clock.</summary>
    public int TickAt(double ms) => (TickMs.BinarySearch(ms) is var b && b >= 0 ? b + 1 : ~b) - 1;
}
