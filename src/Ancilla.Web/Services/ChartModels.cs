namespace Ancilla.Web.Services;

public enum SeriesKind
{
    /// <summary>One dot per column.</summary>
    Dots,
    /// <summary>A 2px line through the columns.</summary>
    Line,
    /// <summary>A line with a dot on every point (for sparse series like CSR).</summary>
    LineDots,
    /// <summary>Bars from zero; colour by sign (Color above zero, NegColor below).</summary>
    Bars,
}

/// <summary>
/// One series. Colours are CSS custom-property names (e.g. "--viz-1") so light/dark swap in CSS.
/// Values align with the chart's columns; null = no value for that column.
/// </summary>
public sealed record ChartSeries(string Name, string Color, SeriesKind Kind, double?[] Values, Func<double, string> Format)
{
    public string? NegColor { get; init; }
    public string? PosLabel { get; init; }
    public string? NegLabel { get; init; }
}

/// <summary>One x position (a match). Title and Note head the tooltip; Href makes it a link.</summary>
public sealed record ChartColumn(string Title, string AxisLabel, string? Note = null, string? Href = null);

/// <summary>A row in a dot plot: one value per panel, plus tooltip detail.</summary>
public sealed record DotRow(string Label, int Count, double[] Values, IReadOnlyList<string> Detail);

/// <summary>A dot-plot panel: its own axis, a reference line (e.g. your overall average) and formatting.</summary>
public sealed record DotPanel(string Title, double Min, double Max, double? Ref, string? RefLabel, Func<double, string> Format, string Color);

public sealed record ScatterGroup(string Name, string Color);

/// <summary>A scatter point. Group indexes into the chart's groups.</summary>
public sealed record ScatterPoint(double X, double Y, int Group, string Title, string? Note, string? Href);

/// <summary>A column: Value null means "no data" for that bucket.</summary>
public sealed record ColumnBucket(string Label, string? SubLabel, double? Value, IReadOnlyList<string> Detail);
