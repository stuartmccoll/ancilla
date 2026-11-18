namespace Ancilla.Theater.Format;

/// <summary>A position in film units (scale and axes unknown; Z is vertical). Z is always even.</summary>
public readonly record struct FilmPosition(int X, int Y, int Z)
{
    /// <summary>Horizontal distance, in film units.</summary>
    public double HorizontalDistance(FilmPosition o) => Math.Sqrt((double)(X - o.X) * (X - o.X) + (double)(Y - o.Y) * (Y - o.Y));
}

/// <summary>
/// How many bits each coordinate of a position takes. It depends on the map: 18/15/18 on most maps
/// seen, 20/17/17 on Bazaar, 21/18/16 on one Slayer map. Z's lowest bit is always 0 (checked on all
/// three). <see cref="Detect"/> reads a film's layout from its records; where the widths come from
/// isn't known (FORMAT.md, "Field widths depend on the map").
/// </summary>
public sealed record PositionLayout(int XBits, int YBits, int ZBits)
{
    public static readonly PositionLayout Standard = new(18, 15, 18);
    public int Bits => XBits + YBits + ZBits;

    /// <summary>
    /// The layout of a film's positions, from its type-0 payloads in tick order. Two steps, both on
    /// biped records holding only a position and the command tick:
    /// <list type="number">
    /// <item>The position's length: the one after which the command tick lands and equals the packet
    /// count plus a per-film offset (a 1-in-256 chance otherwise) in by far the most records.</item>
    /// <item>The split: between ticks a coordinate's low bits flip often and its high bits hardly ever,
    /// so each field ends where a bit flipping in 5% or more of steps is followed by one flipping in
    /// under 1%.</item>
    /// </list>
    /// Returns null when either step isn't clear-cut (too few records, or no single answer).
    /// </summary>
    public static PositionLayout? Detect(IReadOnlyList<ReadOnlyMemory<byte>> payloads, Action<string>? log = null)
    {
        const int MinData = 2 + 3 * 10 + 1, MaxData = 2 + 3 * 24 + 1, Span = MaxData + 9, MaxRecords = 20_000;
        // (tick, slot, the bits from the position's start) for [0, 25] biped records, up to MaxRecords
        // in a row (enough for both steps, and consecutive, so slots have records a tick apart).
        var records = new List<(int Tick, int Slot, byte[] B, int At)>();
        for (var t = 0; t < payloads.Count && records.Count < MaxRecords; t++)
        {
            var b = EntityRecords.Expand(payloads[t].Span);
            for (var i = 0; i + 32 + Span <= b.Length; i++)
                if (EntityRecords.IsBipedHeader(b, i) && EntityRecords.Read(b, i + 15, 5) == 2
                    && EntityRecords.Read(b, i + 20, 6) == EntityRecords.PositionComponent && EntityRecords.Read(b, i + 26, 6) == EntityRecords.CommandTickComponent
                    && b[i + 32] == 0 && b[i + 33] == 0)
                    records.Add((t, EntityRecords.Read(b, i, 13), b[(i + 32)..(i + 32 + Span)], 0));
        }
        if (records.Count < 200) { log?.Invoke($"{records.Count} position-and-tick records, too few"); return null; }

        // 1. Length: count records per (length, tick offset); score each length by its four most common offsets.
        var scores = new List<(int Length, int Score, int[] Offsets)>();
        for (var w = MinData; w <= MaxData; w++)
        {
            var offsets = new Dictionary<int, int>();
            foreach (var (t, _, b, at) in records)
                if (b[at + w] == 1) { var o = (EntityRecords.Read(b, at + w + 1, 8) - t) & 0xFF; offsets[o] = offsets.GetValueOrDefault(o) + 1; }
            var top = offsets.OrderByDescending(x => x.Value).Take(4).ToList();
            scores.Add((w, top.Sum(x => x.Value), top.Select(x => x.Key).ToArray()));
        }
        scores.Sort((a, b) => b.Score.CompareTo(a.Score));
        var best = scores[0];
        if (best.Score * 2 < records.Count || best.Score < 5 * scores[1].Score)
        { log?.Invoke($"length unclear: {best.Length} bits fits {best.Score} of {records.Count}, {scores[1].Length} bits {scores[1].Score}"); return null; }
        var length = best.Length;

        // 2. Split: flip rates of each coordinate bit between a slot's records one tick apart, using
        // only records whose tick checked.
        var good = records.Where(r => r.B[r.At + length] == 1 && best.Offsets.Contains((EntityRecords.Read(r.B, r.At + length + 1, 8) - r.Tick) & 0xFF)).ToList();
        var coordinateBits = length - 3;
        var flips = new int[coordinateBits];
        var pairs = 0;
        foreach (var g in good.GroupBy(r => r.Slot))
            foreach (var (a, c) in g.Zip(g.Skip(1)))
            {
                if (c.Tick - a.Tick != 1) continue;
                pairs++;
                for (var k = 0; k < coordinateBits; k++) if (a.B[a.At + 2 + k] != c.B[c.At + 2 + k]) flips[k]++;
            }
        if (pairs < 100) { log?.Invoke($"{pairs} pairs of records a tick apart, too few"); return null; }
        // X's and Y's ends; the drop into Z's last bit (always 0) isn't a field end.
        var ends = Enumerable.Range(0, coordinateBits - 2).Where(k => flips[k] * 20 >= pairs && flips[k + 1] * 100 < pairs).ToList();
        if (ends.Count != 2 || flips[coordinateBits - 1] * 100 >= pairs)   // Z's lowest bit is always 0
        {
            log?.Invoke($"split unclear ({length} bits, {pairs} pairs): flip rates " + string.Join(" ", flips.Select(f => $"{100.0 * f / pairs:F1}")));
            return null;
        }
        int x = ends[0] + 1, y = ends[1] - ends[0];
        return new PositionLayout(x, y, coordinateBits - x - y);
    }
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
/// A biped's velocity (component 1), in film units per second: a unit direction and a speed.
/// Stored as <c>[00] [sign:1] [direction:18] [speed:10]</c> (31 bits): the direction is a point on a
/// cube's face, <c>face × 87,381 + u × 294 + v</c>, where face 0, 1 or 2 is the axis with the largest
/// component (X, Y, Z), sign is that component's sign, and u and v (0–293, 146.5 is 0) are the other
/// two components divided by it, in X, Y, Z order. The speed is a small float, a 4-bit exponent and a
/// 6-bit mantissa; its value is half a film unit per second (checked against the change in position
/// on 12 films). A velocity of just <c>01</c> (2 bits) means standing still (<see cref="Still"/>).
/// </summary>
public readonly record struct BipedVelocity(double X, double Y, double Z)
{
    public const int Bits = 31, StillBits = 2, FaceSize = 87_381, Steps = 294;
    public const double FilmUnitsPerSecondPerStep = 0.5;
    public static readonly BipedVelocity Still = new(0, 0, 0);

    public double Speed => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double HorizontalSpeed => Math.Sqrt(X * X + Y * Y);

    /// <summary>The 29 bits after the <c>00</c>, or null when the direction is out of range.</summary>
    public static BipedVelocity? Decode(int sign, int direction, int speed)
    {
        var face = direction / FaceSize;
        var rest = direction - face * FaceSize;
        int u = rest / Steps, v = rest % Steps;
        if (face > 2 || u >= Steps) return null;
        double a = u / ((Steps - 1) / 2.0) - 1, b = v / ((Steps - 1) / 2.0) - 1, d = sign == 1 ? -1 : 1;
        var (x, y, z) = face == 0 ? (d, a, b) : face == 1 ? (a, d, b) : (a, b, d);
        var n = Math.Sqrt(x * x + y * y + z * z);
        int e = speed >> 6, m = speed & 63;
        var steps = e == 0 ? m : (64 + m) << (e - 1);
        var s = steps * FilmUnitsPerSecondPerStep / n;
        return new BipedVelocity(x * s, y * s, z * s);
    }
}

/// <summary>
/// What one inventory slot holds, from a <c>weapon-state-type-info</c> component (43–46). It starts
/// with a presence bit. When the slot holds something, <see cref="Id"/> names the item (the same in
/// every film; a tag ID, probably) and <see cref="Variant"/> is the 32 bits after it: shared by
/// weapons of one kind, and for the CTF flag, which team's flag it is. When the slot is empty,
/// <see cref="Id"/> is null and <see cref="Stamped"/> says whether a tick stamp follows: an empty
/// slot at the start of play has none, and a flag leaving the slot (a capture or a drop) has one.
/// Only the first type-info component in a record is read.
/// </summary>
public readonly record struct InventoryItem(int Component, uint? Id, uint? Variant = null, bool Stamped = false)
{
    public bool IsEmpty => Id is null;
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
    FilmPosition? Position, Aim? Aim, int? CommandTick, int? AmmoComponent, int? Rounds, BipedVelocity? Velocity = null,
    InventoryItem? Item = null);

/// <summary>
/// Entity records in type-0 (per-tick) packets. Each record updates one entity, addressed by its
/// type-1 slot, and lists the components it carries by index in the header registry's block for
/// that entity class (block 35 for bipeds). MSB-first:
/// <code>
/// [slot:13] [0][1] [n:5] n × [component:6, strictly ascending] then per-component data
/// component 0 (position), bipeds:          [00] [X] [Y] [Z]   18/15/18 bits on most maps (PositionLayout)
/// component 0 (position), dropped objects:      [X] [Y] [Z]
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

    /// <summary>Biped component indexes (registry block 35) whose layout is known.</summary>
    public const int PositionComponent = 0, VelocityComponent = 1, ShieldComponent = 5, AimComponent = 21, CommandTickComponent = 25;

    /// <summary>Weapon-state-ammo components, one per inventory slot (probably).</summary>
    public static readonly int[] AmmoComponents = [30, 33, 36, 39];
    /// <summary>Weapon-state-rounds-inventory components (reserve rounds, probably), one per inventory slot.</summary>
    public static readonly int[] ReserveComponents = [31, 34, 37, 40];
    public const int DesiredWeaponSetComponent = 42, FirstTypeInfoComponent = 43, TypeInfoComponents = 4;

    // Bits each component takes, as far as it's known (FORMAT.md, "Biped component widths").
    // Position is [00] + 51 coordinate bits + 1 bit; velocity is 31 bits when it starts [00].
    private const int VelocityBits = BipedVelocity.Bits, ShieldBits = 29, AimBits = 25, CommandTickBits = 9;
    private const int ReserveBits = 11, DesiredWeaponSetBits = 7, ItemIdBits = 32;

    // [0][1] [n = 4] components 0, 1, 2, 3: position, velocity, forward-and-up, angular velocity.
    private static readonly byte[] DroppedObjectHeader = Bits("01" + "00100" + "000000" + "000001" + "000010" + "000011");

    /// <summary>
    /// Candidate biped and dropped-object positions in one type-0 payload. <paramref name="layout"/> is
    /// the film's (<see cref="PositionLayout.Detect"/>); the default is <see cref="PositionLayout.Standard"/>.
    /// Dropped objects are assumed to use the bipeds' layout.
    /// </summary>
    public static (List<EntityPosition> Bipeds, List<EntityPosition> Objects) Find(ReadOnlySpan<byte> payload, PositionLayout? layout = null)
    {
        var l = layout ?? PositionLayout.Standard;
        var b = Expand(payload);
        var bipeds = new List<EntityPosition>();
        var objects = new List<EntityPosition>();
        var n = b.Length;
        for (var i = 0; i + SlotBits + 2 + CountBits <= n; i++)
        {
            // Biped slots 512–767 are 13-bit values starting 00010.
            if (IsBipedHeader(b, i))
            {
                var count = Read(b, i + 15, CountBits);
                var data = i + 20 + ComponentBits * count;
                if (count >= 1 && data + 2 + l.Bits <= n && Read(b, i + 20, ComponentBits) == 0
                    && Ascending(b, i + 20, count) && b[data] == 0 && b[data + 1] == 0)
                    bipeds.Add(new EntityPosition(Read(b, i, SlotBits), Position(b, data + 2, l)));
            }
            // Dropped objects: the slot, then the fixed header, then the position.
            var at = i + SlotBits;
            if (at + DroppedObjectHeader.Length + l.Bits <= n && b.AsSpan(at, DroppedObjectHeader.Length).SequenceEqual(DroppedObjectHeader))
                objects.Add(new EntityPosition(Read(b, i, SlotBits), Position(b, at + DroppedObjectHeader.Length, l)));
        }
        return (bipeds, objects);
    }

    /// <summary>
    /// Biped records in one type-0 payload, each read component by component as far as the layout
    /// is known:
    /// <code>
    /// 0  position      [00] [X:18] [Y:15] [Z:18] [1 bit]
    /// 1  velocity      [00] [sign:1] [direction:18] [speed:10] (<see cref="BipedVelocity"/>), or [01]: standing still
    /// 5  shield        29 bits, not decoded
    /// 21 aim           [1] [yaw:12] [pitch:12]
    /// 25 command tick  [1] [tick mod 256:8], then a [0] when more components follow
    /// 30/33/36/39 ammo [0] [rounds:8], [10] [value:12] (share of a battery, probably), or [11]
    /// 31/34/37/40      reserve rounds (probably), 11 bits, not decoded
    /// 42               desired weapon set, 7 bits ([?:3] [inventory slot wanted:3] [?:1]), not decoded
    /// 43–46 type-info  [1] [item ID:32] [variant:32] …, or [0] [stamped:1] … when empty (<see cref="InventoryItem"/>)
    /// </code>
    /// Reading stops at the first component it can't read. Positions use <paramref name="layout"/>, the
    /// film's (default <see cref="PositionLayout.Standard"/>); with the wrong one nothing checks. Like <see cref="Find"/>, it matches headers at every
    /// bit offset; use <see cref="BipedUpdate.Checked"/> updates where chance matches would matter.
    /// </summary>
    public static List<BipedUpdate> FindBipedUpdates(ReadOnlySpan<byte> payload, PositionLayout? layout = null)
    {
        var l = layout ?? PositionLayout.Standard;
        var b = Expand(payload);
        var updates = new List<BipedUpdate>();
        var n = b.Length;
        for (var i = 0; i + SlotBits + 2 + CountBits <= n; i++)
        {
            if (!IsBipedHeader(b, i)) continue;
            var count = Read(b, i + 15, CountBits);
            var q = i + 20 + ComponentBits * count;
            if (count < 1 || q > n || !Ascending(b, i + 20, count)) continue;
            var components = Enumerable.Range(0, count).Select(k => Read(b, i + 20 + ComponentBits * k, ComponentBits)).ToArray();
            if (ReadBiped(b, q, components, l) is { } u) updates.Add(u with { Slot = Read(b, i, SlotBits) });
        }
        return updates;
    }

    // Reads one biped record's components from bit q; null when its command tick doesn't land.
    private static BipedUpdate? ReadBiped(byte[] b, int q, int[] components, PositionLayout layout)
    {
        var positionDataBits = 2 + layout.Bits + 1;   // [00], the coordinates, 1 bit
        FilmPosition? position = null; Aim? aim = null; BipedVelocity? velocity = null; InventoryItem? item = null;
        int? tick = null, ammoComponent = null, rounds = null;
        bool Has(int bits) => q + bits <= b.Length;
        for (var k = 0; k < components.Length; k++)
        {
            var c = components[k];
            if (c == PositionComponent && Has(positionDataBits) && b[q] == 0 && b[q + 1] == 0)
            { position = Position(b, q + 2, layout); q += positionDataBits; }
            else if (c == VelocityComponent && Has(VelocityBits) && b[q] == 0 && b[q + 1] == 0)
            {
                velocity = BipedVelocity.Decode(b[q + 2], Read(b, q + 3, 18), Read(b, q + 21, 10));
                q += VelocityBits;
            }
            else if (c == VelocityComponent && Has(BipedVelocity.StillBits) && b[q] == 0 && b[q + 1] == 1)
            { velocity = BipedVelocity.Still; q += BipedVelocity.StillBits; }
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
            else if (AmmoComponents.Contains(c) && Has(14))
            {
                // The first round count in the record is the one kept.
                if (b[q] == 0) { if (rounds is null) (ammoComponent, rounds) = (c, Read(b, q + 1, 8)); q += 9; }
                else q += b[q + 1] == 1 ? 2 : 14;
            }
            else if (ReserveComponents.Contains(c) && Has(ReserveBits)) q += ReserveBits;
            else if (c == DesiredWeaponSetComponent && Has(DesiredWeaponSetBits)) q += DesiredWeaponSetBits;
            else if (c >= FirstTypeInfoComponent && c < FirstTypeInfoComponent + TypeInfoComponents && Has(1 + 2 * ItemIdBits))
            {
                item = b[q] == 1
                    ? new InventoryItem(c, ReadU32(b, q + 1), ReadU32(b, q + 1 + ItemIdBits))
                    : new InventoryItem(c, null, Stamped: b[q + 1] == 1);
                break;   // the rest of the type-info isn't decoded
            }
            else break;
        }
        if (position is null && aim is null && velocity is null && tick is null && rounds is null) return null;
        return new BipedUpdate(0, components, tick is not null, position, aim, tick, ammoComponent, rounds, velocity, item);
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

    private static FilmPosition Position(byte[] b, int at, PositionLayout l) =>
        new(Read(b, at, l.XBits), Read(b, at + l.XBits, l.YBits), Read(b, at + l.XBits + l.YBits, l.ZBits));

    // Biped slots 512–767 are 13-bit values starting 00010; then [0][1].
    internal static bool IsBipedHeader(byte[] b, int i) =>
        b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1 && b[i + 4] == 0 && b[i + 13] == 0 && b[i + 14] == 1;

    private static bool Ascending(byte[] b, int at, int count)
    {
        for (var q = 1; q < count; q++)
            if (Read(b, at + ComponentBits * q, ComponentBits) <= Read(b, at + ComponentBits * (q - 1), ComponentBits)) return false;
        return true;
    }

    private static uint ReadU32(byte[] b, int at) => (uint)Read(b, at, 16) << 16 | (uint)Read(b, at + 16, 16);

    internal static int Read(byte[] b, int at, int bits)
    {
        var v = 0;
        for (var i = 0; i < bits; i++) v = (v << 1) | b[at + i];
        return v;
    }

    /// <summary>One byte (0 or 1) per bit, MSB-first, so headers can be matched at any bit offset.</summary>
    internal static byte[] Expand(ReadOnlySpan<byte> payload)
    {
        var b = new byte[payload.Length * 8];
        for (var i = 0; i < payload.Length; i++)
            for (var k = 0; k < 8; k++) b[i * 8 + k] = (byte)((payload[i] >> (7 - k)) & 1);
        return b;
    }

    private static byte[] Bits(string s) => s.Select(c => (byte)(c - '0')).ToArray();
}
