using Ancilla.Theater.Format;

namespace Ancilla.Theater;

/// <summary>
/// Biped and dropped-object positions from a cached film's type-0 packets, with each tick's time on
/// the summary's event clock (packet time from the film's first packet; see FORMAT.md).
/// </summary>
public sealed record FilmPositions(
    List<double> TickMs, List<(int Tick, EntityPosition Record)> Bipeds, List<(int Tick, EntityPosition Record)> Objects)
{
    public static FilmPositions Load(FilmCache cache, string matchId)
    {
        var m = cache.LoadManifest(matchId);
        var ticks = new List<double>();
        var bipeds = new List<(int, EntityPosition)>();
        var objects = new List<(int, EntityPosition)>();
        ulong? firstUs = null;
        foreach (var c in m.CustomData.Chunks.Where(c => c.ChunkType == 2).OrderBy(c => c.Index))
            foreach (var p in PacketReader.Read(cache.LoadChunk(matchId, c), out _))
            {
                firstUs ??= p.TimestampUs;
                if (p.Type != 0) continue;
                var (b, o) = EntityRecords.Find(p.Payload.Span);
                bipeds.AddRange(b.Select(r => (ticks.Count, r)));
                objects.AddRange(o.Select(r => (ticks.Count, r)));
                ticks.Add((p.TimestampUs - firstUs.Value) / 1000.0);
            }
        return new FilmPositions(ticks, EntityRecords.FilterBipeds(bipeds, objects), objects);
    }

    /// <summary>The last tick at or before a time on the event clock.</summary>
    public int TickAt(double ms) => (TickMs.BinarySearch(ms) is var b && b >= 0 ? b + 1 : ~b) - 1;
}
