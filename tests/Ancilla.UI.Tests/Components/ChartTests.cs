using Ancilla.Core;
using Ancilla.UI.Components.Shared;
using Ancilla.UI.Services;
using Bunit;

namespace Ancilla.UI.Tests.Components;

/// <summary>Every chart has a "Show as table" alternative with the same figures (CLAUDE.md).</summary>
public sealed class ChartTests : ComponentTest
{
    private static string TableSummary<T>(IRenderedComponent<T> cut) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.Find("details.table-view > summary").TextContent.Trim();

    private static readonly ChartColumn[] Columns =
    [
        new("Win · Slayer on Live Fire", "1 Nov"),
        new("Loss · Oddball on Streets", "2 Nov", "Joined late"),
        new("Win · Capture the Flag on Aquarius", "3 Nov", Href: "matches/x"),
    ];

    [Fact]
    public void Match_chart_shows_a_legend_and_a_newest_first_table()
    {
        var cut = Render<MatchChart>(p => p
            .Add(x => x.Title, "K/D by match")
            .Add(x => x.Columns, Columns)
            .Add(x => x.Series, [
                new ChartSeries("Match K/D", "--viz-1", SeriesKind.Dots, [1.5, null, 0.8], v => v.ToString("F2")),
                new ChartSeries("5-game K/D", "--viz-2", SeriesKind.Line, [1.5, 1.2, 1.1], v => v.ToString("F2")),
            ]));

        Assert.Equal("Show as table", TableSummary(cut));
        Assert.Equal(["Match K/D", "5-game K/D"], cut.FindAll("ul.legend li").Select(li => li.TextContent.Trim()));
        var rows = cut.FindAll("details.table-view tbody tr");
        Assert.Equal(3, rows.Count);
        Assert.Equal("Win · Capture the Flag on Aquarius", rows[0].Children[0].TextContent);
        Assert.Equal("—", rows[1].Children[1].TextContent);   // no value for that match
        Assert.Equal("1.50", rows[2].Children[1].TextContent);
    }

    [Fact]
    public void Match_chart_with_one_series_needs_no_legend()
    {
        var cut = Render<MatchChart>(p => p
            .Add(x => x.Title, "CSR")
            .Add(x => x.Columns, Columns)
            .Add(x => x.Series, [new ChartSeries("CSR", "--viz-1", SeriesKind.LineDots, [1400, 1409, 1402], v => v.ToString("N0"))]));
        Assert.Empty(cut.FindAll("ul.legend"));
    }

    [Fact]
    public void Bars_coloured_by_sign_get_both_legend_entries()
    {
        var cut = Render<MatchChart>(p => p
            .Add(x => x.Title, "Kills vs expected")
            .Add(x => x.Columns, Columns)
            .Add(x => x.Series, [new ChartSeries("Kills vs expected", "--viz-1", SeriesKind.Bars, [2, -1.5, 0.5], v => v.ToString("+0.0;-0.0;0"))
                { NegColor = "--viz-neg", PosLabel = "More kills than expected", NegLabel = "Fewer kills than expected" }]));
        Assert.Equal(["More kills than expected", "Fewer kills than expected"], cut.FindAll("ul.legend li").Select(li => li.TextContent.Trim()));
        Assert.Contains("var(--viz-neg)", cut.Markup);
    }

    [Fact]
    public void Empty_charts_say_so()
    {
        var cut = Render<MatchChart>(p => p.Add(x => x.Title, "K/D").Add(x => x.Columns, []).Add(x => x.Series, [])
            .Add(x => x.Empty, "No PvP games yet."));
        Assert.Equal("No PvP games yet.", cut.Find("p.muted").TextContent);
        Assert.Empty(cut.FindAll("details"));
    }

    [Fact]
    public void Column_chart_labels_each_column_for_screen_readers()
    {
        var cut = Render<ColumnChart>(p => p
            .Add(x => x.Title, "Win rate by mode")
            .Add(x => x.ValueName, "Win rate")
            .Add(x => x.CategoryName, "Mode")
            .Add(x => x.Buckets, [
                new ColumnBucket("Slayer", "8 games", 62.4, ["5 wins", "3 losses"]),
                new ColumnBucket("Oddball", null, null, []),
            ]));
        var cols = cut.FindAll(".col");
        Assert.Equal("Slayer: 62%. 5 wins. 3 losses", cols[0].GetAttribute("aria-label"));
        Assert.Equal("Oddball: no games. ", cols[1].GetAttribute("aria-label"));
        Assert.Equal("Show as table", TableSummary(cut));
        var rows = cut.FindAll("details.table-view tbody tr");
        Assert.Equal("5 wins · 3 losses", rows[0].Children[2].TextContent);
        Assert.Equal("—", rows[1].Children[1].TextContent);
        Assert.Equal(["Mode", "Win rate", "Detail"], cut.FindAll("details.table-view th").Select(th => th.TextContent));
    }

    [Fact]
    public void Dot_plot_has_a_table_with_every_row()
    {
        var cut = Render<DotPlot>(p => p
            .Add(x => x.Title, "By map")
            .Add(x => x.CategoryName, "Map")
            .Add(x => x.Rows, [
                new DotRow("Live Fire", 6, [1.2, 55], ["6 games"]),
                new DotRow("Streets", 2, [0.9, 40], ["2 games"]),
            ])
            .Add(x => x.Panels, [
                new DotPanel("K/D", 0, 2, 1.0, "overall 1.00", v => v.ToString("F2"), "--viz-1"),
                new DotPanel("Win rate", 0, 100, 50, null, v => Fmt.Pct(v, 0), "--viz-1"),
            ]));
        Assert.Equal("Show as table", TableSummary(cut));
        var rows = cut.FindAll("details.table-view tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["Streets", "2", "0.90", "40%"], rows[1].Children.Select(c => c.TextContent));
        Assert.Contains("Fewer than 3 games", cut.Find("ul.legend").TextContent);
    }

    [Fact]
    public void Scatter_plot_counts_points_above_the_line()
    {
        var cut = Render<ScatterPlot>(p => p
            .Add(x => x.Title, "Kills against expectations")
            .Add(x => x.Groups, [new ScatterGroup("Win", "--viz-1"), new ScatterGroup("Loss", "--viz-2")])
            .Add(x => x.Points, [
                new ScatterPoint(10, 14, 0, "Slayer on Live Fire", null, null),
                new ScatterPoint(12, 9, 1, "Oddball on Streets", null, null),
            ]));
        Assert.Contains("1 of 2 games (50%) above the line.", cut.Find("p.hint").TextContent);
        Assert.Equal("Show as table", TableSummary(cut));
        Assert.Equal(["Slayer on Live Fire", "Win", "10.0", "14", "+4.0"], cut.FindAll("details.table-view tbody tr")[0].Children.Select(c => c.TextContent));
    }

    [Fact]
    public void Career_forecast_needs_history_then_shows_a_table()
    {
        var day0 = DateTimeOffset.UtcNow.AddDays(-20);
        var none = Render<CareerForecast>(p => p.Add(x => x.Snapshots, [new ProgressSnapshot(day0, 100, 1_000_000)]).Add(x => x.XpTotal, 9_000_000));
        Assert.Contains("Not enough history yet", none.Markup);
        Assert.Contains("(1 so far", none.Markup);

        ProgressSnapshot[] snaps = [new(day0, 100, 1_000_000), new(day0.AddDays(10), 140, 1_100_000), new(day0.AddDays(20), 180, 1_200_000)];
        var cut = Render<CareerForecast>(p => p.Add(x => x.Snapshots, snaps).Add(x => x.XpTotal, 9_000_000));
        Assert.Equal("Show as table", TableSummary(cut));
        var rows = cut.FindAll("details.table-view tbody tr");
        Assert.Equal(3, rows.Count);
        Assert.Equal("1,200,000", rows[0].Children[1].TextContent);   // newest first
        Assert.Contains("10,000", cut.Find(".fc-tiles").TextContent);    // XP per day
    }

    [Fact]
    public void Sparkline_needs_two_points()
    {
        Assert.Equal("", Render<Sparkline>(p => p.Add(x => x.Values, [1.0]).Add(x => x.Label, "K/D")).Markup.Trim());
        var cut = Render<Sparkline>(p => p.Add(x => x.Values, [0.8, 1.2, 1.5]).Add(x => x.Ref, 1.0).Add(x => x.Label, "K/D trend"));
        Assert.Equal("K/D trend", cut.Find(".spark").GetAttribute("aria-label"));
        Assert.Equal(3, cut.FindAll(".hits span").Count);
        Assert.Single(cut.FindAll("line.ref"));
    }
}
