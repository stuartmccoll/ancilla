using System.Buffers.Binary;
using System.Text;
using Ancilla.Theater.Format;
using static Ancilla.Theater.Tests.Synthetic;

namespace Ancilla.Theater.Tests;

public class FilmHeaderTests
{
    [Fact]
    public void Parses_version_registry_table_and_build()
    {
        var h = FilmHeader.Parse(Header([], out var streamOffset));
        Assert.Equal(41u, h.Version);
        var block = Assert.Single(h.Blocks);
        var slot = Assert.Single(block.Slots);
        Assert.Equal("object-position-component", slot.Name);
        Assert.Equal(1u, slot.Value);
        Assert.Equal([1u, 2u, 3u], h.Table);
        Assert.Equal(new BuildIdentity("6.10026.19225.0", "HI_1_13_0", "release", 269225, 8844991), h.Build);
        Assert.Equal(streamOffset, h.StreamOffset);
    }

    [Fact]
    public void An_empty_block_does_not_end_the_registry()
    {
        var bytes = new List<byte>();
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); bytes.AddRange(b); }
        byte[] Block(string? name)
        {
            var b = new byte[FilmHeader.BlockSize];
            if (name is not null) { Encoding.ASCII.GetBytes(name, b); b[256] = 2; }
            return b;
        }
        U32(41); U32(1);
        bytes.AddRange(Block("unit-equipment-component"));
        bytes.AddRange(Block(null));
        bytes.AddRange(Block("weapon-ammo-component"));
        U32(5);
        foreach (var s in new[] { "1.2.3.4", "HI_1_0_0", "release" }) { var b = new byte[32]; Encoding.ASCII.GetBytes(s, b); bytes.AddRange(b); }
        U32(0); U32(0);

        var h = FilmHeader.Parse(bytes.ToArray());
        Assert.Equal(3, h.Blocks.Count);
        Assert.Empty(h.Blocks[1].Slots);
        Assert.Equal("weapon-ammo-component", Assert.Single(h.Blocks[2].Slots).Name);
        Assert.Equal([5u], h.Table);
        Assert.Equal("1.2.3.4", h.Build.Version);
    }
}

public class RosterTests
{
    private static List<RosterEntry> Named(byte[] header, int streamOffset) =>
        Roster.Find(header, streamOffset).Where(r => r.Gamertag is not null).ToList();

    [Fact]
    public void Finds_each_player_with_their_name_in_header_order()
    {
        var header = Header([
            new HeaderPlayer(Xuid(1), "Test Alpha"),
            new HeaderPlayer(Xuid(2), "Test Bravo", ShortRecordOffset),
            new HeaderPlayer(Xuid(3), "Charlie Test"),
        ], out var offset);

        var named = Named(header, offset);
        Assert.Equal(["Test Alpha", "Test Bravo", "Charlie Test"], named.Select(r => r.Gamertag));
        Assert.Equal([Xuid(1), Xuid(2), Xuid(3)], named.Select(r => r.Xuid));
        Assert.Equal([LongRecordOffset, ShortRecordOffset, LongRecordOffset], named.Select(r => (int)(r.NameBit!.Value - r.XuidBit)));
    }

    [Fact]
    public void Finds_a_xuid_above_the_old_upper_bound()
    {
        const ulong high = 0x0009_9000_0000_0001UL;   // above 2,535,999,999,999,999, which once missed a player
        var header = Header([new HeaderPlayer(high, "High Xuid")], out var offset);
        Assert.Equal(high, Assert.Single(Named(header, offset)).Xuid);
    }

    [Fact]
    public void Accepts_a_first_copy_with_stray_bytes_after_the_nul()
    {
        var dirty = Roster.NameField("Stray");
        dirty[30] = (byte)'x';
        var header = Header([new HeaderPlayer(Xuid(1), "Stray", FirstCopyField: dirty)], out var offset);
        Assert.Equal("Stray", Assert.Single(Named(header, offset)).Gamertag);
    }

    [Fact]
    public void Ignores_a_name_outside_the_record_layout_windows()
    {
        var header = Header([new HeaderPlayer(Xuid(1), "Wrong Place", FirstCopyOffset: 5_000)], out var offset);
        Assert.Empty(Named(header, offset));
        Assert.Contains(Roster.Find(header, offset), r => r.Xuid == Xuid(1));
    }

    [Fact]
    public void Name_field_is_16_utf16_characters_zero_padded()
    {
        var f = Roster.NameField("Ab");
        Assert.Equal(32, f.Length);
        Assert.Equal(new byte[] { (byte)'A', 0, (byte)'b', 0 }, f[..4]);
        Assert.All(f[4..], b => Assert.Equal(0, b));
        Assert.Equal(Roster.NameField("ABCDEFGHIJKLMNOP"), Roster.NameField("ABCDEFGHIJKLMNOPQRS"));
    }

    [Fact]
    public void DecodeName_reads_up_to_the_first_nul_and_reports_cleanliness()
    {
        Assert.Equal(("Clean Name", true), Roster.DecodeName(Roster.NameField("Clean Name")));
        var dirty = Roster.NameField("Dirty");
        dirty[31] = 7;
        Assert.Equal(("Dirty", false), Roster.DecodeName(dirty));
    }

    [Theory]
    [InlineData("Ab")]        // fewer than 3 characters
    [InlineData("Bad_Name")]  // '_' isn't a gamertag character
    [InlineData("")]
    public void DecodeName_rejects_what_cannot_be_a_gamertag(string text) =>
        Assert.Null(Roster.DecodeName(Roster.NameField(text)));

    [Fact]
    public void Gamertag_characters_are_letters_digits_space_and_hash()
    {
        Assert.All("aZ09 #", c => Assert.True(Roster.IsGamertagChar(c)));
        Assert.All("_-.\0é", c => Assert.False(Roster.IsGamertagChar(c)));
    }
}
