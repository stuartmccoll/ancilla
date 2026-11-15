namespace Ancilla.Theater.Format;

/// <summary>
/// Reads bits most-significant first within each byte, which is how the header's roster
/// stream is packed (a 64-bit XUID there reads correctly only in this order).
/// </summary>
public sealed class BitReader(ReadOnlyMemory<byte> data, long bitPosition = 0)
{
    public long Position { get; set; } = bitPosition;
    public long Length => (long)data.Length * 8;

    public ulong Read(int bits)
    {
        if (bits is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(bits));
        if (Position + bits > Length) throw new EndOfStreamException();
        var span = data.Span;
        ulong v = 0;
        for (var i = 0; i < bits; i++)
        {
            var b = span[(int)(Position >> 3)];
            v = (v << 1) | (uint)((b >> (7 - (int)(Position & 7))) & 1);
            Position++;
        }
        return v;
    }

    public byte[] ReadBytes(int count)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++) bytes[i] = (byte)Read(8);
        return bytes;
    }
}
