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

    [Fact]
    public void Exceptions_are_written_with_their_stack_traces_indented_and_redacted()
    {
        var log = new DiagnosticLog(_dir, new Redactor());
        Exception thrown;
        try { throw new InvalidOperationException($"Bad match for xuid({Spartans.NobleSix.Xuid})", new FormatException("inner")); }
        catch (Exception ex) { thrown = ex; }

        log.Write("Unhandled exception", thrown);

        var lines = File.ReadAllLines(Directory.EnumerateFiles(_dir).Single());
        Assert.EndsWith("Unhandled exception", lines[0]);
        Assert.Contains("    System.InvalidOperationException: Bad match for xuid([xuid])", lines[1]);
        Assert.Contains(lines, l => l.StartsWith("     ---> System.FormatException: inner", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("at Ancilla.Core.Tests.DiagnosticsTests", StringComparison.Ordinal));
        Assert.All(lines.Skip(1), l => Assert.StartsWith("    ", l));
        Assert.DoesNotContain(Spartans.NobleSix.Xuid, string.Join("\n", lines));
    }

    [Fact]
    public async Task A_response_that_cannot_be_read_fails_like_a_request_and_is_logged_by_endpoint()
    {
        var redactor = new Redactor();
        var log = new DiagnosticLog(_dir, redactor);
        var client = new HaloWaypointClient(_halo.CreateClient(), Spartans.Token, new HaloDiagnostics(redactor, log));
        var match = HaloWorld.Ids.Match(0);
        _halo.Respond(u => u.AbsolutePath.EndsWith("/skill"), """{"Value":[{"Id":"x","ResultCode":0,"Result":{"TeamMmr":null}}]}""");
        _halo.Respond(u => u.AbsolutePath.EndsWith("/stats"), """{"MatchId":null}""");

        var skill = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMatchSkillAsync(match, [Spartans.NobleSix.Xuid]));
        var stats = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMatchStatsAsync(match));

        Assert.Equal("Couldn't read the response from skill/hi/matches/{id}/skill.", skill.Message);
        Assert.IsType<System.Text.Json.JsonException>(skill.InnerException);
        Assert.Equal("Couldn't read the response from halostats/hi/matches/{id}/stats.", stats.Message);
        var text = string.Concat(Directory.EnumerateFiles(_dir).Select(File.ReadAllText));
        Assert.Contains("skill/hi/matches/{id}/skill response couldn't be read ($.Value[0].Result.TeamMmr)", text);
        Assert.Contains("halostats/hi/matches/{id}/stats response couldn't be read (missing fields)", text);
        Assert.DoesNotContain(match, text);
    }

    [Fact]
    public void Recent_errors_are_read_back_newest_first_with_their_details_and_nothing_else()
    {
        var redactor = new Redactor();
        var log = new DiagnosticLog(_dir, redactor);
        log.Write("Ancilla started");
        log.Error("Unhandled exception in the page", Thrown("first"));
        log.Write("halostats/hi/matches/{id}/stats 200 120 ms");
        log.Error("Error in Test: no exception");
        log.Error("Unobserved task exception", Thrown("third"));
        log.Write("    an indented line after a plain one isn't an error's detail");

        var errors = log.RecentErrors();

        Assert.Equal(["Unobserved task exception", "Error in Test: no exception", "Unhandled exception in the page"], errors.Select(e => e.Message));
        Assert.StartsWith("System.InvalidOperationException: third", errors[0].Detail[0]);
        Assert.Contains(errors[0].Detail, l => l.TrimStart().StartsWith("at Ancilla.Core.Tests.DiagnosticsTests", StringComparison.Ordinal));
        Assert.Empty(errors[1].Detail);
        Assert.True(DateTimeOffset.UtcNow - errors[0].At < TimeSpan.FromMinutes(1));
        Assert.Equal(["Unobserved task exception"], log.RecentErrors(1).Select(e => e.Message));
    }

    [Fact]
    public void Recent_errors_are_redacted_again_and_span_rolled_files()
    {
        var redactor = new Redactor();
        var log = new DiagnosticLog(_dir, redactor, maxBytes: 600);
        log.Error($"Couldn't load {Spartans.Kat.Gamertag}'s matches");   // not yet known to be a gamertag
        for (var i = 0; i < 10; i++) log.Error($"Error {i}", Thrown($"number {i}"));
        Assert.True(Directory.EnumerateFiles(_dir).Count() > 1);   // rolled

        redactor.Register(Spartans.Kat.Gamertag);
        var errors = log.RecentErrors(20);

        Assert.Equal(11, errors.Count);
        Assert.Equal("Error 9", errors[0].Message);
        Assert.Equal("Couldn't load [redacted]'s matches", errors[^1].Message);
        Assert.Empty(new DiagnosticLog(Path.Combine(_dir, "none"), redactor).RecentErrors());
    }

    private static Exception Thrown(string message)
    {
        try { throw new InvalidOperationException(message); }
        catch (Exception ex) { return ex; }
    }

    [Fact]
    public void Clearing_deletes_the_log_files_and_then_the_folder()
    {
        var log = new DiagnosticLog(_dir, new Redactor());
        log.Write("Signed in (Pasted)");
        Assert.NotEqual(0, log.SizeBytes());
        log.Clear();
        Assert.False(Directory.Exists(_dir));
        log.Clear();   // nothing there: no error

        log.Write("Started");   // a new file after clearing
        Assert.Single(Directory.EnumerateFiles(_dir));
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "not a log");
        log.Clear();
        Assert.Equal([Path.Combine(_dir, "keep.txt")], Directory.EnumerateFileSystemEntries(_dir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
