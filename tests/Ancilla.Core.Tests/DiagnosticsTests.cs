using Ancilla.Testing;

namespace Ancilla.Core.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ancilla-diag-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHalo _halo = new();

    [Fact]
    public void Endpoint_names_hold_no_ids()
    {
        var xuid = Spartans.NobleSix.Xuid;
        var match = HaloWorld.Ids.Match(0);
        var name = HaloDiagnostics.EndpointName($"https://halostats.svc.halowaypoint.com/hi/players/xuid({xuid})/matches?start=0&count=25");
        Assert.Equal("halostats/hi/players/xuid({xuid})/matches", name);
        Assert.Equal("halostats/hi/matches/{id}/stats", HaloDiagnostics.EndpointName($"https://halostats.svc.halowaypoint.com/hi/matches/{match}/stats"));
    }

    [Fact]
    public async Task Calls_are_counted_and_logged_by_endpoint_without_ids_or_the_token()
    {
        var redactor = new Redactor();
        var log = new DiagnosticLog(_dir, redactor);
        var diagnostics = new HaloDiagnostics(redactor, log);
        var client = new HaloWaypointClient(_halo.CreateClient(), Spartans.Token, diagnostics);

        var me = await client.GetMeAsync();
        await client.GetMatchHistoryAsync(me.Xuid, 0, 5, MatchHistoryType.All);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMatchStatsAsync(Guid.NewGuid().ToString()));

        var endpoints = diagnostics.Endpoints();
        Assert.Contains(endpoints, e => e.Endpoint == "halostats/hi/players/xuid({xuid})/matches" && e.Calls == 1 && e.Errors == 0 && e.LastStatus == 200);
        Assert.Contains(endpoints, e => e.Endpoint == "halostats/hi/matches/{id}/stats" && e.Errors == 1);

        var text = string.Concat(Directory.EnumerateFiles(_dir).Select(File.ReadAllText));
        Assert.Contains("halostats/hi/players/xuid({xuid})/matches 200", text);
        Assert.DoesNotContain(me.Xuid, text);
        Assert.DoesNotContain(Spartans.Token["v4=".Length..], text);
        Assert.DoesNotContain("start=0", text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
