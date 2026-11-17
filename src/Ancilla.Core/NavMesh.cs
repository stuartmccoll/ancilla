using System.Buffers.Binary;
using System.IO.Compression;

namespace Ancilla.Core;

/// <summary>One walkable polygon of a navmesh, in world units. Z is up.</summary>
public sealed record NavFace(IReadOnlyList<(float X, float Y, float Z)> Points)
{
    public float Height => Points.Average(p => p.Z);
}

/// <summary>
/// A Forge map's navigation mesh: the walkable floor the game's AI uses, as polygons in world units.
/// Read from the map asset's <c>navmesh.blob</c>. Undocumented; the layout below was worked out from
/// the files (see Ancilla.Theater's FORMAT.md, "Map geometry"):
/// <code>
/// [u32 BE 2] [u32 BE length] [u32 BE 0x001FFFFF] [12 bytes] raw deflate:
///   [u32 BE 2] [u32 BE sections] [u32 BE part sizes…] then Havok 2022.1 tagfiles, the first an
///   hkaiNavMesh: chunks [u32 BE size | flags] [4-char tag], with TAG0 holding SDKV, DATA, TYPE and
///   INDX; INDX's ITEM entries (12 bytes LE: type and flags, offset into DATA, count) 2, 3 and 4 are
///   the faces, edges and vertices.
/// face   12 bytes: start edge i32, start user edge i32, edge count i16, user edge count i16
/// edge   20 bytes: vertex a i32, vertex b i32, then opposite edge, opposite face, flags, cost
/// vertex 16 bytes: x, y, z, w as float32
/// </code>
/// </summary>
public sealed record NavMesh(IReadOnlyList<NavFace> Faces)
{
    private const int FaceSize = 12, EdgeSize = 20, VertexSize = 16, HeaderSize = 24;
    private const int MaxInflated = 64 << 20;

    /// <summary>Parses a <c>navmesh.blob</c>. Throws <see cref="InvalidDataException"/> when the layout isn't the one known.</summary>
    public static NavMesh Parse(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < HeaderSize + 2 || BinaryPrimitives.ReadUInt32BigEndian(blob) != 2)
            throw new InvalidDataException("Not a navmesh blob.");
        var data = Inflate(blob[HeaderSize..].ToArray());

        // The navmesh tagfile: the first TAG0 whose type strings start with hkaiNavMesh.
        for (var at = IndexOf(data, "TAG0"u8, 0); at >= 4; at = IndexOf(data, "TAG0"u8, at + 4))
        {
            var size = (int)(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at - 4)) & 0x3FFF_FFFF);
            if (size < 8 || at - 4 + size > data.Length) continue;
            var tag = data.AsSpan(at - 4, size);
            var names = IndexOf(tag, "TST1"u8, 0);
            if (names < 0 || !tag[(names + 4)..].StartsWith("hkaiNavMesh\0"u8)) continue;
            return ReadNavMesh(tag);
        }
        throw new InvalidDataException("No hkaiNavMesh in the blob.");
    }

    private static NavMesh ReadNavMesh(ReadOnlySpan<byte> tag)
    {
        var dataTag = IndexOf(tag, "DATA"u8, 0);
        var itemTag = IndexOf(tag, "ITEM"u8, 0);
        if (dataTag < 4 || itemTag < 4) throw new InvalidDataException("Navmesh tagfile has no DATA or ITEM.");
        var dataSize = (int)(BinaryPrimitives.ReadUInt32BigEndian(tag[(dataTag - 4)..]) & 0x3FFF_FFFF) - 8;
        var itemSize = (int)(BinaryPrimitives.ReadUInt32BigEndian(tag[(itemTag - 4)..]) & 0x3FFF_FFFF) - 8;
        if (dataSize < 0 || dataTag + 4 + dataSize > tag.Length || itemSize < 5 * 12 || itemTag + 4 + itemSize > tag.Length)
            throw new InvalidDataException("Navmesh tagfile chunks are out of range.");
        var data = tag.Slice(dataTag + 4, dataSize);
        var items = tag.Slice(itemTag + 4, itemSize);

        var (fo, fc) = Item(items, data.Length, 2, FaceSize);
        var (eo, ec) = Item(items, data.Length, 3, EdgeSize);
        var (vo, vc) = Item(items, data.Length, 4, VertexSize);

        var vertices = new (float X, float Y, float Z)[vc];
        for (var i = 0; i < vc; i++)
        {
            var v = data[(vo + i * VertexSize)..];
            vertices[i] = (BinaryPrimitives.ReadSingleLittleEndian(v), BinaryPrimitives.ReadSingleLittleEndian(v[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(v[8..]));
            if (!float.IsFinite(vertices[i].X) || !float.IsFinite(vertices[i].Y) || !float.IsFinite(vertices[i].Z))
                throw new InvalidDataException("Navmesh vertex isn't a number.");
        }
        var faces = new List<NavFace>(fc);
        for (var i = 0; i < fc; i++)
        {
            var f = data[(fo + i * FaceSize)..];
            int start = BinaryPrimitives.ReadInt32LittleEndian(f), n = BinaryPrimitives.ReadInt16LittleEndian(f[8..]);
            if (n < 3 || start < 0 || start + n > ec) throw new InvalidDataException("Navmesh face is out of range.");
            var points = new (float, float, float)[n];
            for (var j = 0; j < n; j++)
            {
                var e = data[(eo + (start + j) * EdgeSize)..];
                int a = BinaryPrimitives.ReadInt32LittleEndian(e), b = BinaryPrimitives.ReadInt32LittleEndian(e[4..]);
                var next = BinaryPrimitives.ReadInt32LittleEndian(data[(eo + (start + (j + 1) % n) * EdgeSize)..]);
                // A face's edges chain round it: each edge ends where the next starts.
                if (a < 0 || a >= vc || b != next) throw new InvalidDataException("Navmesh face edges don't form a polygon.");
                points[j] = vertices[a];
            }
            faces.Add(new NavFace(points));
        }
        if (faces.Count == 0) throw new InvalidDataException("Navmesh has no faces.");
        return new NavMesh(faces);
    }

    // ITEM entry i: its offset into DATA and element count, checked against DATA's length.
    private static (int Offset, int Count) Item(ReadOnlySpan<byte> items, int dataLength, int i, int stride)
    {
        var offset = BinaryPrimitives.ReadInt32LittleEndian(items[(i * 12 + 4)..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(items[(i * 12 + 8)..]);
        if (offset < 0 || count < 0 || (long)offset + (long)count * stride > dataLength)
            throw new InvalidDataException("Navmesh array is out of range.");
        return (offset, count);
    }

    private static byte[] Inflate(byte[] deflated)
    {
        try
        {
            using var z = new DeflateStream(new MemoryStream(deflated), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = z.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxInflated) throw new InvalidDataException("Navmesh is too large.");
            }
            return output.ToArray();
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException) { throw new InvalidDataException("Navmesh isn't deflate-compressed.", ex); }
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int from) =>
        from >= haystack.Length ? -1 : haystack[from..].IndexOf(needle) is var i and >= 0 ? from + i : -1;
}
