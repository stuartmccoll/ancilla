using Ancilla.Theater.Format;

namespace Ancilla.Theater;

/// <summary>
/// Biped and dropped-object positions from a cached film's type-0 packets, with each tick's time on
/// the summary's event clock (packet time from the film's first packet; see FORMAT.md). Aims are
/// from biped records whose command tick checked, for the biped slots kept in <see cref="Bipeds"/>.
/// <see cref="Lists"/> is each tick's per-player list (<see cref="PlayerList.Parse"/>; null where it
/// didn't parse). <see cref="InventoryChanges"/> are the kept bipeds' inventory changes
/// (<see cref="Inventory.Changes"/>). <see cref="Layout"/> is the film's position layout
/// (<see cref="PositionLayout.Detect"/>), or <see cref="PositionLayout.Standard"/> when it couldn't be
/// detected; <see cref="LayoutDetected"/> says which.
/// </summary>
public sealed record FilmPositions(
    List<double> TickMs, List<(int Tick, EntityPosition Record)> Bipeds, List<(int Tick, EntityPosition Record)> Objects,
    List<(int Tick, int Slot, Aim Aim)> Aims, List<int[]?> Lists, List<InventoryChange>? InventoryChanges = null,
    PositionLayout? Layout = null, bool LayoutDetected = false)
{
    public static FilmPositions Load(FilmCache cache, string matchId)
    {
        var m = cache.LoadManifest(matchId);
        var ticks = new List<double>();
        var bipeds = new List<(int, EntityPosition)>();
        var objects = new List<(int, EntityPosition)>();
        var aims = new List<(int Tick, int Slot, Aim Aim)>();
        var lists = new List<int[]?>();
        var updates = new List<(int Tick, BipedUpdate Update)>();
        ulong? firstUs = null;
        var packets = new List<Packet>();
        foreach (var c in m.CustomData.Chunks.Where(c => c.ChunkType == 2).OrderBy(c => c.Index))
            foreach (var p in PacketReader.Read(cache.LoadChunk(matchId, c), out _))
            {
                firstUs ??= p.TimestampUs;
                if (p.Type == 0) packets.Add(p);
            }
        var detected = PositionLayout.Detect(packets.Select(p => p.Payload).ToList());
        var layout = detected ?? PositionLayout.Standard;
        foreach (var p in packets)
        {
            var (b, o) = EntityRecords.Find(p.Payload.Span, layout);
            bipeds.AddRange(b.Select(r => (ticks.Count, r)));
            objects.AddRange(o.Select(r => (ticks.Count, r)));
            foreach (var u in EntityRecords.FindBipedUpdates(p.Payload.Span, layout))
            {
                if (u.Checked && u.Aim is { } a) aims.Add((ticks.Count, u.Slot, a));
                if (u.Checked && u.Components[^1] >= Inventory.FirstTypeInfoComponent) updates.Add((ticks.Count, u));
            }
            lists.Add(PlayerList.Parse(p.Payload.Span));
            ticks.Add((p.TimestampUs - firstUs!.Value) / 1000.0);
        }
        var kept = EntityRecords.FilterBipeds(bipeds, objects);
        var slots = kept.Select(r => r.Record.Slot).ToHashSet();
        return new FilmPositions(ticks, kept, objects, aims.Where(a => slots.Contains(a.Slot)).ToList(), lists,
            Inventory.Changes(updates).Where(c => slots.Contains(c.Slot)).ToList(), layout, detected is not null);
    }

    /// <summary>Each player's alive spans from the per-player list, given the summary's deaths (time, player).</summary>
    public List<AliveSpan> AliveSpans(IEnumerable<(double Ms, int Player)> deaths) =>
        PlayerList.Spans(PlayerList.Presence(Lists), deaths.Select(d => (TickAt(d.Ms), d.Player)).ToList());

    /// <summary>The last tick at or before a time on the event clock.</summary>
    public int TickAt(double ms) => (TickMs.BinarySearch(ms) is var b && b >= 0 ? b + 1 : ~b) - 1;
}
