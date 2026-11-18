namespace Ancilla.Theater.Format;

/// <summary>
/// A change to what a biped holds in one inventory slot: a checked biped record listing that slot's
/// <c>weapon-state-type-info</c> component (43–46 in registry block 35). <see cref="InventorySlot"/>
/// is 0–3. Slots 0 and 1 are the two weapons; slot 2 is the CTF flag (FORMAT.md, "Weapon pickups").
/// <see cref="Item"/> is what the slot now holds (<see cref="InventoryItem.Id"/>), when one of the
/// change's records could be read that far, with its <see cref="Variant"/>. <see cref="Emptied"/>: the
/// slot was emptied (with a tick stamp: a flag captured or dropped; see <see cref="InventoryItem"/>).
/// </summary>
public readonly record struct InventoryChange(int Tick, int Slot, int InventorySlot, uint? Item = null, bool Emptied = false, uint? Variant = null)
{
    public bool IsWeapon => InventorySlot is 0 or 1;
}

/// <summary>
/// Finds weapon pickups. A dropped weapon doesn't vanish when it's picked up: dropped objects stop
/// sending records when they come to rest, and their slots are freed later (median 23–28 s, about
/// when they'd despawn anyway). What does show is the picker's inventory: the slot's type-info
/// component is sent (<see cref="Changes"/>), and with both weapon slots full, the weapon given up
/// is dropped at the player as a new object (<see cref="SwapDrop"/>).
/// </summary>
public static class Inventory
{
    public const int FirstTypeInfoComponent = EntityRecords.FirstTypeInfoComponent, Slots = EntityRecords.TypeInfoComponents;
    /// <summary>Records for one slot this close together (ticks, 1 s) are one change.</summary>
    public const int MergeTicks = 60;
    /// <summary>
    /// Weapon IDs read in two or more of the 16 cached films (2026-11-18), by how often they were
    /// picked up. Which weapon each one is isn't known; FORMAT.md ("Which weapon") has what each one's
    /// ammo looks like. Used by <c>inspect</c> to check that IDs are read where they start.
    /// </summary>
    public static readonly IReadOnlySet<uint> KnownWeapons = new HashSet<uint>
    {
        0x841ac5e5, 0x4ff3937e, 0x850cc95e, 0xb8d58516, 0x9564e17f, 0xda99cabf, 0xdaf193c7, 0xc30d87c7, 0xfa040c87,
        0xd04aaf4f, 0x958c126a, 0xc9c3d45c, 0x30484ea6, 0xdb0cec25, 0x918223d8, 0xfecc2aa6, 0x71ab0a2c, 0x86906234,
        0xc04bbdd2, 0x230447b1, 0xa460ce96, 0xa0955e9e, 0xc25e94f6, 0xc3542946, 0xbb3edcb6, 0x0a1992bc,
    };

    /// <summary>The CTF flag's ID in the flag slot (type-info 45), in every CTF film.</summary>
    public const uint FlagId = 0x2a392328;

    /// <summary>How near (film units, horizontally) and how soon (ticks) a swapped-out weapon lands.</summary>
    public const double SwapDistance = 80;
    public const int SwapTicks = 60;

    /// <summary>
    /// Inventory changes from checked biped updates (tick, update), one per biped, inventory slot and
    /// second: a change is often sent in several records in a row.
    /// </summary>
    public static List<InventoryChange> Changes(IEnumerable<(int Tick, BipedUpdate Update)> updates)
    {
        var all = updates.Where(u => u.Update.Checked)
            .SelectMany(u => u.Update.Components
                .Where(c => c >= FirstTypeInfoComponent && c < FirstTypeInfoComponent + Slots)
                // An empty slot without a stamp is the slot's state at the start of play, not a change.
                .Where(c => u.Update.Item is not { IsEmpty: true, Stamped: false } it || it.Component != c)
                .Select(c => u.Update.Item is { } it && it.Component == c
                    ? new InventoryChange(u.Tick, u.Update.Slot, c - FirstTypeInfoComponent, it.Id, it.IsEmpty, it.Variant)
                    : new InventoryChange(u.Tick, u.Update.Slot, c - FirstTypeInfoComponent)))
            .OrderBy(c => c.Tick).ToList();
        // Per (biped, inventory slot): the tick of its latest record and its change's index in the result.
        var last = new Dictionary<(int, int), (int Tick, int Index)>();
        var result = new List<InventoryChange>();
        foreach (var c in all)
        {
            var key = (c.Slot, c.InventorySlot);
            // Records of one change say the same thing; a pickup and an emptying are different changes.
            if (last.TryGetValue(key, out var l) && c.Tick - l.Tick <= MergeTicks && result[l.Index].Emptied == c.Emptied)
            {
                last[key] = (c.Tick, l.Index);
                if (result[l.Index].Item is null && c.Item is not null) result[l.Index] = result[l.Index] with { Item = c.Item, Variant = c.Variant };
                continue;
            }
            last[key] = (c.Tick, result.Count);
            result.Add(c);
        }
        return result;
    }

    /// <summary>
    /// Whether a dropped object first appears within <see cref="SwapDistance"/> of
    /// <paramref name="at"/> and <see cref="SwapTicks"/> of <paramref name="tick"/>, other than at a
    /// death (objects dropped from 0.1 s before to 1.5 s after one): the weapon a pickup replaced.
    /// <paramref name="objectsFirst"/> is each dropped object's first record.
    /// </summary>
    public static bool SwapDrop(FilmPosition at, int tick, IEnumerable<(int Tick, EntityPosition Record)> objectsFirst, IReadOnlyList<int> deathTicks) =>
        objectsFirst.Any(o => Math.Abs(o.Tick - tick) <= SwapTicks && o.Record.Position.HorizontalDistance(at) <= SwapDistance
            && !deathTicks.Any(d => o.Tick - d is >= -6 and <= 90));
}
