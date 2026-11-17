namespace Ancilla.Theater.Format;

/// <summary>A position in film units (scale and axes unknown; Z is vertical). Z is always even.</summary>
public readonly record struct FilmPosition(int X, int Y, int Z)
{
    /// <summary>Horizontal distance, in film units.</summary>
    public double HorizontalDistance(FilmPosition o) => Math.Sqrt((double)(X - o.X) * (X - o.X) + (double)(Y - o.Y) * (Y - o.Y));
}

/// <summary>One entity's position in one type-0 packet.</summary>
public readonly record struct EntityPosition(int Slot, FilmPosition Position);

/// <summary>
/// Where a biped is aiming (component 21): 4,096 units to a full turn on both angles. Yaw is
/// measured from +X towards +Y, so turning right lowers it. Pitch is 2,048 when level (probably;
/// it's the midpoint and the value at spawn) and higher when looking up.
/// </summary>
public readonly record struct Aim(int Yaw, int Pitch)
{
    public const int UnitsPerTurn = 4096, Level = 2048;
    public double YawDegrees => Yaw * 360.0 / UnitsPerTurn;
    /// <summary>Degrees above level; negative is looking down.</summary>
    public double PitchDegrees => (Pitch - Level) * 360.0 / UnitsPerTurn;
}

/// <summary>
/// One biped record from a type-0 packet, read as far as its components are understood.
/// <see cref="Checked"/> is true when the record's command tick landed where the widths before it
/// say it should (a `1` bit, and a `0` bit after it when more components follow). Records whose
/// components before the tick aren't understood, and records with only an ammo component, have
/// no tick to check, so they're candidates only: they can be chance matches.
/// </summary>
public sealed record BipedUpdate(
    int Slot, IReadOnlyList<int> Components, bool Checked,
    FilmPosition? Position, Aim? Aim, int? CommandTick, int? AmmoComponent, int? Rounds);

/// <summary>
/// Entity records in type-0 (per-tick) packets. Each record updates one entity, addressed by its
/// type-1 slot, and lists the components it carries by index in the header registry's block for
/// that entity class (block 35 for bipeds). MSB-first:
/// <code>
/// [slot:13] [0][1] [n:5] n × [component:6, strictly ascending] then per-component data
/// component 0 (position), bipeds:          [00] [X:18] [Y:15] [Z:18]
/// component 0 (position), dropped objects:      [X:18] [Y:15] [Z:18]
/// </code>
/// Record boundaries aren't decoded in general, so this finds records by their header: biped
/// records by slot range (512–767) and a component list starting with 0, dropped objects (weapons,
/// grenades, equipment) by their fixed component list 0–3. Either can match by chance, so callers
/// filter by coordinate range and by how often a slot recurs (see <see cref="FilterBipeds"/>).
/// Checked by research scripts on ten PvP films: deaths end the victim's biped track, dropped
/// objects land on it, and new biped slots appear 8 s later at the team's base.
/// </summary>
public static class EntityRecords
{
    public const int SlotBits = 13, CountBits = 5, ComponentBits = 6;
    public const int XBits = 18, YBits = 15, ZBits = 18, PositionBits = XBits + YBits + ZBits;

    /// <summary>Biped component indexes (registry block 35) whose layout is known.</summary>
    public const int PositionComponent = 0, VelocityComponent = 1, ShieldComponent = 5, AimComponent = 21, CommandTickComponent = 25;

    /// <summary>Weapon-state-ammo components, one per inventory slot (probably).</summary>
    public static readonly int[] AmmoComponents = [30, 33, 36, 39];

    // Bits each component takes, as far as it's known (FORMAT.md, "Biped component widths").
    // Position is [00] + 51 coordinate bits + 1 bit; velocity is 31 bits only after a position.
    private const int PositionDataBits = 2 + PositionBits + 1, VelocityBits = 31, ShieldBits = 29, AimBits = 25, CommandTickBits = 9;

    // [0][1] [n = 4] components 0, 1, 2, 3: position, velocity, forward-and-up, angular velocity.
    private static readonly byte[] DroppedObjectHeader = Bits("01" + "00100" + "000000" + "000001" + "000010" + "000011");

    /// <summary>Candidate biped and dropped-object positions in one type-0 payload.</summary>
    public static (List<EntityPosition> Bipeds, List<EntityPosition> Objects) Find(ReadOnlySpan<byte> payload)
    {
        var b = Expand(payload);
        var bipeds = new List<EntityPosition>();
        var objects = new List<EntityPosition>();
        var n = b.Length;
        for (var i = 0; i + SlotBits + 2 + CountBits <= n; i++)
        {
            // Biped slots 512–767 are 13-bit values starting 00010.
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1 && b[i + 4] == 0 && b[i + 13] == 0 && b[i + 14] == 1)
            {
                var count = Read(b, i + 15, CountBits);
                var data = i + 20 + ComponentBits * count;
                if (count >= 1 && data + 2 + PositionBits <= n && Read(b, i + 20, ComponentBits) == 0
                    && Ascending(b, i + 20, count) && b[data] == 0 && b[data + 1] == 0)
                    bipeds.Add(new EntityPosition(Read(b, i, SlotBits), Position(b, data + 2)));
            }
            // Dropped objects: the slot, then the fixed header, then the position.
            var at = i + SlotBits;
            if (at + DroppedObjectHeader.Length + PositionBits <= n && b.AsSpan(at, DroppedObjectHeader.Length).SequenceEqual(DroppedObjectHeader))
                objects.Add(new EntityPosition(Read(b, i, SlotBits), Position(b, at + DroppedObjectHeader.Length)));
        }
        return (bipeds, objects);
    }

    /// <summary>
    /// Biped records in one type-0 payload, each read component by component as far as the layout
    /// is known:
    /// <code>
    /// 0  position      [00] [X:18] [Y:15] [Z:18] [1 bit]
    /// 1  velocity      31 bits, not decoded (only after a position: on its own its width differs)
    /// 5  shield        29 bits, not decoded
    /// 21 aim           [1] [yaw:12] [pitch:12]
    /// 25 command tick  [1] [tick mod 256:8], then a [0] when more components follow
    /// 30/33/36/39 ammo [0] [rounds:8] (a [1] there is another kind of value, not read)
    /// </code>
    /// Reading stops at the first component it can't read. Positions assume 18/15/18 bits, so a map
    /// with other widths (Bazaar) won't check. Like <see cref="Find"/>, it matches headers at every
    /// bit offset; use <see cref="BipedUpdate.Checked"/> updates where chance matches would matter.
    /// </summary>
    public static List<BipedUpdate> FindBipedUpdates(ReadOnlySpan<byte> payload)
    {
        var b = Expand(payload);
        var updates = new List<BipedUpdate>();
        var n = b.Length;
        for (var i = 0; i + SlotBits + 2 + CountBits <= n; i++)
        {
            if (!(b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1 && b[i + 4] == 0 && b[i + 13] == 0 && b[i + 14] == 1)) continue;
            var count = Read(b, i + 15, CountBits);
            var q = i + 20 + ComponentBits * count;
            if (count < 1 || q > n || !Ascending(b, i + 20, count)) continue;
            var components = Enumerable.Range(0, count).Select(k => Read(b, i + 20 + ComponentBits * k, ComponentBits)).ToArray();
            if (ReadBiped(b, q, components) is { } u) updates.Add(u with { Slot = Read(b, i, SlotBits) });
        }
        return updates;
    }

    // Reads one biped record's components from bit q; null when its command tick doesn't land.
    private static BipedUpdate? ReadBiped(byte[] b, int q, int[] components)
    {
        FilmPosition? position = null; Aim? aim = null; int? tick = null, ammoComponent = null, rounds = null;
        bool Has(int bits) => q + bits <= b.Length;
        for (var k = 0; k < components.Length; k++)
        {
            var c = components[k];
            if (c == PositionComponent && Has(PositionDataBits) && b[q] == 0 && b[q + 1] == 0)
            { position = Position(b, q + 2); q += PositionDataBits; }
            else if (c == VelocityComponent && position is not null && Has(VelocityBits)) q += VelocityBits;
            else if (c == ShieldComponent && Has(ShieldBits)) q += ShieldBits;
            else if (c == AimComponent && Has(AimBits) && b[q] == 1)
            { aim = new Aim(Read(b, q + 1, 12), Read(b, q + 13, 12)); q += AimBits; }
            else if (c == CommandTickComponent && Has(CommandTickBits + 1))
            {
                // A misplaced tick means a width before it is wrong or the header was a chance match.
                var more = k + 1 < components.Length;
                if (b[q] != 1 || (more && b[q + CommandTickBits] != 0)) return null;
                tick = Read(b, q + 1, 8);
                q += CommandTickBits + (more ? 1 : 0);
            }
            else if (AmmoComponents.Contains(c) && Has(9))
            {
                if (b[q] == 0) (ammoComponent, rounds) = (c, Read(b, q + 1, 8));
                break;   // what follows the count isn't decoded
            }
            else break;
        }
        if (position is null && aim is null && tick is null && rounds is null) return null;
        return new BipedUpdate(0, components, tick is not null, position, aim, tick, ammoComponent, rounds);
    }

    /// <summary>
    /// Keeps biped candidates that look real: coordinates inside the film's range (taken from
    /// dropped objects, the 0.5–99.5 percentiles widened by 30% plus 100 units), Z even, and a slot
    /// with at least <paramref name="minPerSlot"/> records in the film.
    /// </summary>
    public static List<(int Tick, EntityPosition Record)> FilterBipeds(
        IReadOnlyList<(int Tick, EntityPosition Record)> bipeds, IReadOnlyList<(int Tick, EntityPosition Record)> objects, int minPerSlot = 30)
    {
        if (objects.Count == 0) return [];
        (double Lo, double Hi) Range(Func<FilmPosition, int> axis)
        {
            var v = objects.Select(o => axis(o.Record.Position)).Order().ToList();
            double lo = v[v.Count / 200], hi = v[v.Count - v.Count / 200 - 1], pad = (hi - lo) * 0.3 + 100;
            return (lo - pad, hi + pad);
        }
        var (x, y, z) = (Range(p => p.X), Range(p => p.Y), Range(p => p.Z));
        bool Inside(FilmPosition p) => p.X >= x.Lo && p.X <= x.Hi && p.Y >= y.Lo && p.Y <= y.Hi && p.Z >= z.Lo && p.Z <= z.Hi && p.Z % 2 == 0;
        var kept = bipeds.Where(r => Inside(r.Record.Position)).ToList();
        var perSlot = kept.CountBy(r => r.Record.Slot).ToDictionary();
        return kept.Where(r => perSlot[r.Record.Slot] >= minPerSlot).ToList();
    }

    private static FilmPosition Position(byte[] b, int at) =>
        new(Read(b, at, XBits), Read(b, at + XBits, YBits), Read(b, at + XBits + YBits, ZBits));

    private static bool Ascending(byte[] b, int at, int count)
    {
        for (var q = 1; q < count; q++)
            if (Read(b, at + ComponentBits * q, ComponentBits) <= Read(b, at + ComponentBits * (q - 1), ComponentBits)) return false;
        return true;
    }

    private static int Read(byte[] b, int at, int bits)
    {
        var v = 0;
        for (var i = 0; i < bits; i++) v = (v << 1) | b[at + i];
        return v;
    }

    /// <summary>One byte (0 or 1) per bit, MSB-first, so headers can be matched at any bit offset.</summary>
    private static byte[] Expand(ReadOnlySpan<byte> payload)
    {
        var b = new byte[payload.Length * 8];
        for (var i = 0; i < payload.Length; i++)
            for (var k = 0; k < 8; k++) b[i * 8 + k] = (byte)((payload[i] >> (7 - k)) & 1);
        return b;
    }

    private static byte[] Bits(string s) => s.Select(c => (byte)(c - '0')).ToArray();
}
