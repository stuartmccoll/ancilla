namespace Ancilla.Theater.Format;

/// <summary>
/// The same data shifted left by 0–7 bits (MSB-first), so a byte pattern sitting at any bit
/// offset can be found with an ordinary byte search: bit offset = byteIndex × 8 + shift.
/// </summary>
public sealed class ShiftedBits
{
    private readonly byte[][] _shifted = new byte[8][];

    public ShiftedBits(ReadOnlySpan<byte> data)
    {
        _shifted[0] = data.ToArray();
        for (var s = 1; s < 8; s++)
        {
            var a = new byte[Math.Max(0, data.Length - 1)];
            for (var i = 0; i < a.Length; i++) a[i] = (byte)((data[i] << s) | (data[i + 1] >> (8 - s)));
            _shifted[s] = a;
        }
    }

    /// <summary>The <paramref name="count"/> bytes starting at <paramref name="bit"/>, or empty past the end.</summary>
    public ReadOnlySpan<byte> At(long bit, int count)
    {
        var a = _shifted[bit & 7];
        var i = bit >> 3;
        return i < 0 || i + count > a.Length ? [] : a.AsSpan((int)i, count);
    }

    /// <summary>Every bit offset where <paramref name="pattern"/> occurs, in increasing order.</summary>
    public List<long> FindAll(ReadOnlySpan<byte> pattern)
    {
        var hits = new List<long>();
        for (var s = 0; s < 8; s++)
        {
            var a = _shifted[s].AsSpan();
            for (var from = 0; ;)
            {
                var i = a[from..].IndexOf(pattern);
                if (i < 0) break;
                hits.Add((long)(from + i) * 8 + s);
                from += i + 1;
            }
        }
        hits.Sort();
        return hits;
    }
}
