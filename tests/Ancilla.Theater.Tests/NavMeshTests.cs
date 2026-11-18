using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Ancilla.Core;
using Ancilla.Theater.Format;

namespace Ancilla.Theater.Tests;

public class NavMeshTests
{
    // An L-shaped floor, 20 × 30 world units with a 10 × 20 notch, as two quads and a raised triangle.
    private static readonly (float, float, float)[] Vertices =
        [(0, 0, 50), (20, 0, 50), (20, 10, 50), (0, 10, 50), (10, 30, 50), (0, 30, 50), (10, 10, 50), (2, 2, 54), (4, 2, 54), (2, 4, 54)];
    private static readonly int[][] Faces = [[0, 1, 2, 3], [3, 6, 4, 5], [7, 8, 9]];

    /// <summary>A navmesh blob in the layout NavMesh reads (see NavMesh's summary), built from scratch.</summary>
    internal static byte[] Blob((float X, float Y, float Z)[] vertices, int[][] faces, bool breakChain = false)
    {
        var faceBytes = new List<byte>(); var edgeBytes = new List<byte>(); var vertexBytes = new List<byte>();
        var edge = 0;
        foreach (var f in faces)
        {
            faceBytes.AddRange(I32(edge)); faceBytes.AddRange(I32(-1)); faceBytes.AddRange(I16(f.Length)); faceBytes.AddRange(I16(0));
            for (var j = 0; j < f.Length; j++)
            {
                var b = f[(j + 1) % f.Length] + (breakChain && j == 0 ? 1 : 0);
                edgeBytes.AddRange(I32(f[j])); edgeBytes.AddRange(I32(b)); edgeBytes.AddRange(new byte[12]);
                edge++;
            }
        }
        foreach (var (x, y, z) in vertices) { vertexBytes.AddRange(F32(x)); vertexBytes.AddRange(F32(y)); vertexBytes.AddRange(F32(z)); vertexBytes.AddRange(F32(1)); }

        var data = new List<byte>(new byte[16]);
        var arrays = new[] { faceBytes, edgeBytes, vertexBytes }.Select(a => { var at = data.Count; data.AddRange(a); return at; }).ToArray();
        var items = new List<byte>();
        items.AddRange(new byte[24]);   // entries 0 and 1 (none, the navmesh object)
        int[] counts = [faces.Length, edge, vertices.Length];
        for (var i = 0; i < 3; i++) { items.AddRange(I32(0x2000_0000)); items.AddRange(I32(arrays[i])); items.AddRange(I32(counts[i])); }

        var body = new List<byte>();
        body.AddRange(Chunk("SDKV", Encoding.ASCII.GetBytes("20220100")));
        body.AddRange(Chunk("DATA", data.ToArray()));
        body.AddRange(Chunk("TYPE", Chunk("TST1", Encoding.ASCII.GetBytes("hkaiNavMesh\0hkArray\0"))));
        body.AddRange(Chunk("INDX", Chunk("ITEM", items.ToArray())));
        var tag = Chunk("TAG0", body.ToArray());

        var inner = new List<byte>();
        inner.AddRange(U32Be(2)); inner.AddRange(U32Be(1)); inner.AddRange(U32Be((uint)tag.Length));
        inner.AddRange(tag);
        using var deflated = new MemoryStream();
        using (var z = new DeflateStream(deflated, CompressionLevel.Optimal, leaveOpen: true)) z.Write(inner.ToArray());

        var blob = new List<byte>();
        blob.AddRange(U32Be(2)); blob.AddRange(U32Be((uint)(deflated.Length + 12))); blob.AddRange(U32Be(0x001F_FFFF)); blob.AddRange(new byte[12]);
        blob.AddRange(deflated.ToArray());
        return blob.ToArray();
    }

    private static byte[] Chunk(string tag, byte[] payload) =>
        [.. U32Be((uint)(payload.Length + 8) | 0x4000_0000), .. Encoding.ASCII.GetBytes(tag), .. payload];
    private static byte[] U32Be(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] I32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return b; }
    private static byte[] I16(int v) { var b = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, (short)v); return b; }
    private static byte[] F32(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); return b; }

    [Fact]
    public void Faces_are_read_as_polygons_from_their_chained_edges()
    {
        var mesh = NavMesh.Parse(Blob(Vertices, Faces));

        Assert.Equal(3, mesh.Faces.Count);
        Assert.Equal([(0f, 0f, 50f), (20f, 0f, 50f), (20f, 10f, 50f), (0f, 10f, 50f)], mesh.Faces[0].Points);
        Assert.Equal(54f, mesh.Faces[2].Height);
    }

    [Fact]
    public void Edges_that_dont_chain_round_a_face_are_rejected() =>
        Assert.Throws<InvalidDataException>(() => NavMesh.Parse(Blob(Vertices, Faces, breakChain: true)));

    [Fact]
    public void Blobs_that_arent_navmeshes_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => NavMesh.Parse(new byte[64]));
        var notDeflate = Blob(Vertices, Faces);
        notDeflate.AsSpan(24).Fill(0xFF);
        Assert.Throws<InvalidDataException>(() => NavMesh.Parse(notDeflate));
    }

    [Fact]
    public void Only_files_on_the_public_ugc_blob_host_get_a_url()
    {
        UgcAsset Asset(string prefix) => new("a", "v", "Map", null, new UgcFiles(prefix, [UgcAsset.NavMeshFile]));
        const string ok = "https://blobs-infiniteugc.svc.halowaypoint.com/ugcstorage/map/x/y/";

        Assert.Equal(new Uri(ok + "navmesh.blob"), Asset(ok).BlobFileUri(UgcAsset.NavMeshFile));
        Assert.Null(Asset(ok).BlobFileUri("map.mvar"));                                   // not listed
        Assert.Null(Asset("https://example.com/ugcstorage/").BlobFileUri(UgcAsset.NavMeshFile));
        Assert.Null(Asset("http://blobs-infiniteugc.svc.halowaypoint.com/x/").BlobFileUri(UgcAsset.NavMeshFile));
    }

    [Fact]
    public void A_films_placement_on_the_mesh_is_found()
    {
        var mesh = NavMesh.Parse(Blob(Vertices, Faces));
        const double k = 71.5, ox = 16_250, oy = 16_470;
        // Positions spread over the L, in film units.
        var positions = new List<FilmPosition>();
        for (var x = 0.5; x < 20; x += 0.7)
            for (var y = 0.5; y < 30; y += 0.7)
                if (y < 10 || x < 10) positions.Add(new FilmPosition((int)(ox + k * x), (int)(oy + k * y), 0));

        var fit = FloorPlan.Fit(mesh, positions)!;

        Assert.InRange(fit.OnMesh, 0.99, 1);
        // Positions that stop short of the edges fit a small range of placements; every corner of the
        // mesh must still land within a world unit of where it really is.
        foreach (var (x, y) in new[] { (0.0, 0.0), (20.0, 0.0), (20.0, 10.0), (10.0, 30.0), (0.0, 30.0) })
        {
            var (fx, fy) = fit.ToFilm(x, y);
            Assert.InRange(Math.Abs(fx - (ox + k * x)), 0, k);
            Assert.InRange(Math.Abs(fy - (oy + k * y)), 0, k);
        }
        Assert.Null(FloorPlan.Fit(mesh, []));
    }

    [Fact]
    public void A_placement_far_from_the_usual_origin_is_found_for_another_position_layout()
    {
        var mesh = NavMesh.Parse(Blob(Vertices, Faces));
        const double k = 72, ox = 65_000, oy = 130_000;
        var positions = new List<FilmPosition>();
        for (var x = 0.5; x < 20; x += 0.7)
            for (var y = 0.5; y < 30; y += 0.7)
                if (y < 10 || x < 10) positions.Add(new FilmPosition((int)(ox + k * x), (int)(oy + k * y), 0));

        var fit = FloorPlan.Fit(mesh, positions, new PositionLayout(21, 18, 16))!;
        Assert.InRange(fit.OnMesh, 0.99, 1);
        var (fx, fy) = fit.ToFilm(0, 0);
        Assert.InRange(Math.Abs(fx - ox), 0, k);
        Assert.InRange(Math.Abs(fy - oy), 0, k);
    }

    [Fact]
    public void Floor_faces_away_from_play_are_dropped_and_the_rest_get_height_levels()
    {
        var mesh = NavMesh.Parse(Blob(Vertices, Faces));
        var fit = new FloorFit(1, 0, 0, 1);
        MapPoint ToMap(double x, double y) => new(x, y);
        // Players only went near the first quad's left end (and the raised triangle on it).
        var near = new HashSet<(int, int)> { (1, 1), (2, 2), (3, 3) };

        var floor = MatchTimeline.PlaceFloor(mesh, fit, ToMap, near);

        Assert.Equal(2, floor.Count);                                    // the second quad is too far
        Assert.Equal([1, MatchTimeline.FloorLevels], floor.Select(f => f.Level));   // lowest first
        Assert.All(MatchTimeline.PlaceFloor(NavMesh.Parse(Blob(Vertices, [Faces[0], Faces[1]])), fit, ToMap, near), f => Assert.Equal(1, f.Level));
    }
}
