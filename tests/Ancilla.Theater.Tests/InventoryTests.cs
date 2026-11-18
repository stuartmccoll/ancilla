using Ancilla.Theater.Format;

namespace Ancilla.Theater.Tests;

public class InventoryTests
{
    private static BipedUpdate Update(int slot, bool isChecked, params int[] components) =>
        new(slot, components, isChecked, null, null, isChecked ? 1 : null, null, null);

    [Fact]
    public void A_change_takes_its_item_from_whichever_of_its_records_has_one()
    {
        (int, BipedUpdate)[] updates =
        [
            (100, Update(600, true, 0, 25, 30, 31, 42, 43)),                                   // ID not read
            (110, Update(600, true, 0, 25, 42, 43) with { Item = new InventoryItem(43, 0x1234_5678) }),
            (120, Update(600, true, 0, 25, 44) with { Item = new InventoryItem(44, 0xCAFE_F00D) }),
            (130, Update(600, true, 0, 25, 43, 44) with { Item = new InventoryItem(43, 0x1111_2222) }),   // only the first is read
        ];
        var changes = Inventory.Changes(updates);
        Assert.Equal([new(100, 600, 0, 0x1234_5678), new(120, 600, 1, 0xCAFE_F00D)], changes);
    }

    [Fact]
    public void A_change_sent_in_several_records_counts_once_per_inventory_slot()
    {
        (int, BipedUpdate)[] updates =
        [
            (100, Update(600, true, 0, 25, 30, 31, 42, 43)),   // weapon slot 0
            (110, Update(600, true, 0, 25, 43)),               // the same change, again
            (120, Update(600, true, 0, 25, 42, 44)),           // weapon slot 1
            (130, Update(601, false, 33, 43)),                 // unchecked: ignored
            (300, Update(600, true, 0, 25, 43)),               // a later change to slot 0
            (400, Update(602, true, 0, 25, 45)),               // slot 2: the CTF flag
        ];
        var changes = Inventory.Changes(updates);

        Assert.Equal([new(100, 600, 0), new(120, 600, 1), new(300, 600, 0), new(400, 602, 2)], changes);
        Assert.Equal([true, true, true, false], changes.Select(c => c.IsWeapon));
    }

    [Fact]
    public void A_swapped_out_weapon_is_a_new_object_at_the_player_away_from_deaths()
    {
        var at = new FilmPosition(10_000, 10_000, 200_000);
        (int, EntityPosition)[] objects = [(1_020, new EntityPosition(1_400, new FilmPosition(10_030, 10_040, 200_100)))];

        Assert.True(Inventory.SwapDrop(at, 1_000, objects, deathTicks: [5_000]));
        Assert.False(Inventory.SwapDrop(at, 1_000, objects, deathTicks: [1_000]));                     // dropped at a death
        Assert.False(Inventory.SwapDrop(at, 1_200, objects, deathTicks: []));                          // too long before
        Assert.False(Inventory.SwapDrop(new FilmPosition(10_300, 10_000, 200_000), 1_000, objects, []));   // too far away
    }
}
