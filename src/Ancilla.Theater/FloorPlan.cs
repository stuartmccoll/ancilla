using Ancilla.Core;
using Ancilla.Theater.Format;

namespace Ancilla.Theater;

/// <summary>
/// Where a navmesh sits in a film's coordinates: film X = <see cref="OriginX"/> + <see cref="Scale"/> ×
/// world X, and the same for Y. <see cref="OnMesh"/> is the share of the film's biped positions that
/// land on a navmesh face with that placement.
/// </summary>
public sealed record FloorFit(double Scale, double OriginX, double OriginY, double OnMesh)
{
    public (double X, double Y) ToFilm(double worldX, double worldY) => (OriginX + Scale * worldX, OriginY + Scale * worldY);
}

/// <summary>
/// Lines a map's navmesh up with a film. Film X and Y are world X and Y times about 72, plus about
/// 16,384, but the origin moves by up to a couple of hundred film units from map to map (see
/// FORMAT.md, "Map geometry"), so each film is fitted: the placement that puts the most biped
/// positions on the mesh, searched near those values.
/// </summary>
public static class FloorPlan
{
    /// <summary>Below this share of positions on the mesh, the fit isn't trusted.</summary>
    public const double MinOnMesh = 0.85;
    public const double NominalScale = 72, NominalOrigin = 16_384;
    private const double MinScale = 64, MaxScale = 80, OriginRange = 320;
    private const int MaxCells = 4_000_000, CoarseSample = 600, FineSample = 3_000;

    /// <summary>The best placement, or null when there are no positions.</summary>
    public static FloorFit? Fit(NavMesh mesh, IReadOnlyList<FilmPosition> positions)
    {
        if (positions.Count == 0 || mesh.Faces.Count == 0) return null;
        var cover = Coverage.Of(mesh);
        var coarse = Sample(positions, CoarseSample);
        var fine = Sample(positions, FineSample);

        var best = (Score: -1.0, K: NominalScale, X: NominalOrigin, Y: NominalOrigin);
        for (var k = MinScale; k <= MaxScale; k += 0.5)
            for (var ox = NominalOrigin - OriginRange; ox <= NominalOrigin + OriginRange; ox += 16)
                for (var oy = NominalOrigin - OriginRange; oy <= NominalOrigin + OriginRange; oy += 16)
                    if (cover.Score(coarse, k, ox, oy) is var s && Better(s, k, best.Score, best.K)) best = (s, k, ox, oy);

        var (_, k0, x0, y0) = best;
        best = (cover.Score(fine, k0, x0, y0), k0, x0, y0);
        for (var k = k0 - 0.5; k <= k0 + 0.5; k += 0.1)
            for (var ox = x0 - 16; ox <= x0 + 16; ox += 2)
                for (var oy = y0 - 16; oy <= y0 + 16; oy += 2)
                    if (cover.Score(fine, k, ox, oy) is var s && Better(s, k, best.Score, best.K)) best = (s, k, ox, oy);
        return new FloorFit(best.K, best.X, best.Y, best.Score);
    }

    // More positions on the mesh wins; on a tie (positions that don't reach the mesh's edges fit a
    // range of placements), the scale nearer the nominal one.
    private static bool Better(double score, double k, double bestScore, double bestK) =>
        score > bestScore + 1e-9 || (Math.Abs(score - bestScore) <= 1e-9 && Math.Abs(k - NominalScale) < Math.Abs(bestK - NominalScale));

    private static FilmPosition[] Sample(IReadOnlyList<FilmPosition> all, int n)
    {
        var step = Math.Max(1, all.Count / n);
        var result = new List<FilmPosition>(n + 1);
        for (var i = 0; i < all.Count; i += step) result.Add(all[i]);
        return result.ToArray();
    }

    /// <summary>The mesh's footprint from above, as a grid of covered cells.</summary>
    private sealed class Coverage(double minX, double minY, double cell, int width, int height, bool[] covered)
    {
        public static Coverage Of(NavMesh mesh)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in mesh.Faces.SelectMany(f => f.Points))
                (minX, minY, maxX, maxY) = (Math.Min(minX, p.X), Math.Min(minY, p.Y), Math.Max(maxX, p.X), Math.Max(maxY, p.Y));
            // A quarter of a world unit, coarser for very large meshes.
            var cell = Math.Max(0.25, Math.Sqrt((maxX - minX) * (maxY - minY) / MaxCells));
            int width = (int)((maxX - minX) / cell) + 1, height = (int)((maxY - minY) / cell) + 1;
            var covered = new bool[width * height];
            foreach (var face in mesh.Faces)
            {
                var pts = face.Points;
                int x0 = (int)((pts.Min(p => p.X) - minX) / cell), x1 = (int)((pts.Max(p => p.X) - minX) / cell);
                int y0 = (int)((pts.Min(p => p.Y) - minY) / cell), y1 = (int)((pts.Max(p => p.Y) - minY) / cell);
                for (var gy = y0; gy <= y1; gy++)
                    for (var gx = x0; gx <= x1; gx++)
                        if (Inside(pts, minX + (gx + .5) * cell, minY + (gy + .5) * cell)) covered[gy * width + gx] = true;
            }
            return new Coverage(minX, minY, cell, width, height, covered);
        }

        public double Score(FilmPosition[] positions, double k, double ox, double oy)
        {
            var hits = 0;
            foreach (var p in positions)
            {
                var gx = (int)Math.Floor(((p.X - ox) / k - minX) / cell);
                var gy = (int)Math.Floor(((p.Y - oy) / k - minY) / cell);
                if (gx >= 0 && gx < width && gy >= 0 && gy < height && covered[gy * width + gx]) hits++;
            }
            return (double)hits / positions.Length;
        }

        private static bool Inside(IReadOnlyList<(float X, float Y, float Z)> pts, double x, double y)
        {
            var inside = false;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
                if ((pts[i].Y > y) != (pts[j].Y > y) && x < (pts[j].X - pts[i].X) * (y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
                    inside = !inside;
            return inside;
        }
    }
}
