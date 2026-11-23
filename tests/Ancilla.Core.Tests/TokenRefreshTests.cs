using System.Net;
using Ancilla.Testing;
using static Ancilla.Testing.Spartans;

namespace Ancilla.Core.Tests;

/// <summary>A token that runs out mid-session is replaced through the refresh callback, once.</summary>
public class TokenRefreshTests
{
    private const string Fresh = "v4=cortana-refreshed-token";
    private readonly FakeHalo _halo = new();
    private int _refreshes;

    private HaloWaypointClient Client(Func<Task<string?>> refresh, HaloDiagnostics? diagnostics = null) =>
        new(_halo.CreateClient(), Token, diagnostics, async _ =>
        {
            Interlocked.Increment(ref _refreshes);
            return await refresh();
        });

    [Fact]
    public async Task A_refused_token_is_refreshed_and_the_request_tried_again()
    {
        var client = Client(() => Task.FromResult<string?>(Fresh));
        _halo.AcceptedToken = Fresh;   // the first token has run out

        var me = await client.GetMeAsync();

        Assert.Equal(NobleSix.Xuid, me.Xuid);
        Assert.Equal(1, _refreshes);
        Assert.Equal([Token, Fresh], _halo.Requests.Select(r => r.Headers["x-343-authorization-spartan"]));
        await client.GetServiceRecordAsync(NobleSix.Xuid);
        Assert.Equal(Fresh, _halo.Requests.Last().Headers["x-343-authorization-spartan"]);
        Assert.Equal(1, _refreshes);
    }

    [Fact]
    public async Task Parallel_calls_that_are_refused_share_one_refresh()
    {
        var gate = new TaskCompletionSource();
        var client = Client(async () => { await gate.Task; return Fresh; });
        _halo.AcceptedToken = Fresh;

        var calls = Enumerable.Range(0, 8).Select(_ => client.GetMeAsync()).ToList();
        await Task.Delay(50);
        gate.SetResult();
        await Task.WhenAll(calls);

        Assert.Equal(1, _refreshes);
    }

    [Fact]
    public async Task When_there_is_no_new_token_the_401_stands_and_isnt_retried()
    {
        var client = Client(() => Task.FromResult<string?>(null));
        _halo.AcceptedToken = Fresh;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetServiceRecordAsync(NobleSix.Xuid));

        Assert.Equal(1, _refreshes);   // a sign-in that's gone isn't asked again for every request
        Assert.Equal(2, _halo.Requests.Count);
    }

    [Fact]
    public async Task A_refresh_that_throws_is_a_401_without_the_refresh_error()
    {
        var client = Client(() => throw new InvalidOperationException("web view gone with v4=secret"));
        _halo.AcceptedToken = Fresh;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public async Task Using_a_new_token_after_a_failed_refresh_works_and_can_refresh_again()
    {
        string? next = null;
        var client = Client(() => Task.FromResult(next));
        _halo.AcceptedToken = Fresh;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMeAsync());

        client.UseToken(Fresh);   // signed in again
        await client.GetMeAsync();

        next = "v4=cortana-third-token";
        _halo.AcceptedToken = next;
        await client.GetMeAsync();
        Assert.Equal(2, _refreshes);
    }

    [Fact]
    public async Task Refreshed_tokens_are_redacted_from_logs()
    {
        var redactor = new Redactor();
        var folder = Path.Combine(Path.GetTempPath(), "ancilla-refresh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var diagnostics = new HaloDiagnostics(redactor, new DiagnosticLog(folder, redactor));
            var client = Client(() => Task.FromResult<string?>(Fresh), diagnostics);
            _halo.AcceptedToken = Fresh;
            await client.GetMeAsync();

            Assert.DoesNotContain("cortana-refreshed-token", redactor.Redact($"header {Fresh} and bare cortana-refreshed-token"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_refresh_callback_a_401_is_the_same_as_before()
    {
        _halo.AcceptedToken = Fresh;
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new HaloWaypointClient(_halo.CreateClient(), Token).GetMeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Single(_halo.Requests);
    }
}
