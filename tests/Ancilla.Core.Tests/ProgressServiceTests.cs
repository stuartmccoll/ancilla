using System.Net;
using Ancilla.Testing;

namespace Ancilla.Core.Tests;

public sealed class ProgressServiceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "ancilla-tests-" + Guid.NewGuid().ToString("N"), "snapshots.json");
    private readonly FakeHalo _halo = new();

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_file)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private ProgressService Service() => new(new HaloWaypointClient(_halo.CreateClient(), Spartans.Token), new SnapshotStore(_file));

    [Fact]
    public async Task Builds_a_report_from_career_passes_and_the_service_record()
    {
        var r = await Service().BuildReportAsync();

        Assert.Equal("Noble Six", r.Gamertag);
        Assert.Equal(HaloWorld.MatchmadeCount, r.MatchesCompleted);
        Assert.Equal(12, r.Career.RanksCompleted);
        Assert.Equal(HaloWorld.CareerRanks, r.Career.TotalRanks);
        Assert.False(r.Career.IsHero);
        // Ranks 1–12 cost 500 + r × 250, plus 400 into rank 13.
        Assert.Equal(Enumerable.Range(1, 12).Sum(n => 500 + n * 250) + 400, r.Career.XpEarned);

        // The pass with a missing track file is left out rather than failing the report.
        Assert.Equal(["Operation: Fall of Reach", "Operation: Sacred Icon", "Operation: First Strike"], r.Passes.Select(p => p.Name));
        var active = r.ActivePass!;
        Assert.Equal(7_300, active.XpEarned);
        Assert.True(r.Passes[1].IsComplete);
        Assert.True(r.Passes[2].IsNotStarted);
        Assert.False(r.Passes[2].IsOwned);
    }

    [Fact]
    public async Task Each_report_saves_a_snapshot_once()
    {
        var first = await Service().BuildReportAsync();
        Assert.Single(first.History);
        Assert.Equal(HaloWorld.ActivePassPath, first.History[0].ActivePass);
        Assert.Equal(7_300, first.History[0].ActivePassXpEarned);
        Assert.StartsWith("Lifetime average", first.Estimate.Source);   // nothing to measure from yet
        Assert.Null(first.PassEstimate);

        var second = await Service().BuildReportAsync();
        Assert.Single(second.History);   // nothing changed, so nothing new saved
    }

    [Fact]
    public async Task A_manual_rate_is_used_as_given()
    {
        var r = await Service().BuildReportAsync(xpPerMatchOverride: 900);
        Assert.Equal(900, r.Estimate.XpPerMatch);
    }

    [Fact]
    public async Task A_failed_required_call_fails_the_report()
    {
        _halo.Fail("careerrank1", HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<HttpRequestException>(() => Service().BuildReportAsync());
        Assert.False(File.Exists(_file));
    }
}
