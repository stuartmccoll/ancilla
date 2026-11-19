namespace Ancilla.Core.Tests;

public class HaloCacheTests
{
    [Fact]
    public async Task Concurrent_callers_share_one_request()
    {
        var cache = new HaloCache();
        var calls = 0;
        var gate = new TaskCompletionSource<string>();
        Task<string> Fetch() { Interlocked.Increment(ref calls); return gate.Task; }

        var a = cache.GetOrAddAsync("match:1", Fetch);
        var b = cache.GetOrAddAsync("match:1", Fetch);
        gate.SetResult("Halo");
        Assert.Equal("Halo", await a);
        Assert.Equal("Halo", await b);
        Assert.Equal("Halo", await cache.GetOrAddAsync("match:1", Fetch));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Keys_are_separate()
    {
        var cache = new HaloCache();
        Assert.Equal(1, await cache.GetOrAddAsync("a", () => Task.FromResult(1)));
        Assert.Equal(2, await cache.GetOrAddAsync("b", () => Task.FromResult(2)));
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        var cache = new HaloCache();
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            cache.GetOrAddAsync<string>("map:1", () => throw new HttpRequestException("Covenant jamming")));
        Assert.Equal("Live Fire", await cache.GetOrAddAsync("map:1", () => Task.FromResult("Live Fire")));
    }
}
