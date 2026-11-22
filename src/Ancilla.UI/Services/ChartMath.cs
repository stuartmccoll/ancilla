namespace Ancilla.UI.Services;

public static class ChartMath
{
    /// <summary>A rounded axis domain and ~<paramref name="target"/> evenly spaced ticks (steps of 1, 2, 2.5 or 5 × 10ⁿ).</summary>
    public static (double Min, double Max, double[] Ticks) Nice(double lo, double hi, int target = 4)
    {
        if (double.IsNaN(lo) || double.IsNaN(hi)) (lo, hi) = (0, 1);
        if (hi - lo < 1e-9) { lo -= 1; hi += 1; }
        var raw = (hi - lo) / target;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var norm = raw / mag;
        var step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 2.5 ? 2.5 : norm <= 5 ? 5 : 10) * mag;
        var min = Math.Floor(lo / step) * step;
        var max = Math.Ceiling(hi / step) * step;
        var ticks = new List<double>();
        for (var t = min; t <= max + step / 2; t += step) ticks.Add(Math.Round(t, 6));
        return (min, max, ticks.ToArray());
    }

    /// <summary>Position of <paramref name="v"/> in [min, max] as 0–100 (for left: or bottom: percentages).</summary>
    public static double Pct(double v, double min, double max) => max <= min ? 0 : (v - min) / (max - min) * 100;
}
