using Ancilla.UI.Services;

namespace Ancilla.UI.Tests;

public class ChartMathTests
{
    [Theory]
    [InlineData(0, 10, 0, 10, 2.5)]
    [InlineData(0.3, 1.7, 0, 2, 0.5)]
    [InlineData(-3, 7, -5, 7.5, 2.5)]
    [InlineData(1200, 1530, 1200, 1600, 100)]
    public void Nice_rounds_the_domain_to_even_steps(double lo, double hi, double min, double max, double step)
    {
        var (mn, mx, ticks) = ChartMath.Nice(lo, hi);
        Assert.Equal(min, mn, 9);
        Assert.Equal(max, mx, 9);
        Assert.Equal(step, ticks[1] - ticks[0], 9);
        Assert.Equal(min, ticks[0], 9);
        Assert.Equal(max, ticks[^1], 9);
    }

    [Fact]
    public void Nice_copes_with_flat_and_missing_data()
    {
        var (min, max, ticks) = ChartMath.Nice(5, 5);
        Assert.True(min < 5 && max > 5);
        Assert.NotEmpty(ticks);
        var (nmin, nmax, _) = ChartMath.Nice(double.NaN, double.NaN);
        Assert.Equal(0, nmin);
        Assert.Equal(1, nmax);
    }

    [Fact]
    public void Pct_positions_within_the_domain()
    {
        Assert.Equal(50, ChartMath.Pct(5, 0, 10));
        Assert.Equal(0, ChartMath.Pct(5, 10, 10));
        Assert.Equal(125, ChartMath.Pct(12.5, 0, 10));
    }
}

public class ThemesTests
{
    [Theory]
    [InlineData("halo", "halo")]
    [InlineData("XBOX", "xbox")]
    [InlineData("basic", "basic")]
    [InlineData("covenant", "basic")]
    [InlineData("\"><script>", "basic")]
    [InlineData(null, "basic")]
    public void Only_known_themes_get_through(string? cookie, string id) => Assert.Equal(id, Themes.Normalize(cookie));

    [Fact]
    public void Every_theme_has_three_swatches_and_a_css_block()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ancilla.UI", "wwwroot", "app.css"));
        Assert.Equal(Themes.All.Count, Themes.All.Select(t => t.Id).Distinct().Count());
        foreach (var t in Themes.All)
        {
            Assert.Equal(3, t.Swatches.Length);
            Assert.All(t.Swatches, s => Assert.Matches("^#[0-9a-f]{6}$", s));
            if (t != Themes.Default) Assert.Contains($"[data-theme=\"{t.Id}\"]", css);
        }
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ancilla.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Can't find the repository root.");
    }
}

public class AssetFileProviderTests
{
    [Fact]
    public async Task Serves_fetched_images_to_the_desktop_web_view()
    {
        var cache = new AssetCache();
        var files = new AssetFileProvider(cache);
        Assert.False(files.GetFileInfo("assets/cms/rank.png").Exists);

        await cache.EnsureAsync("cms/rank.png", () => Task.FromResult((new byte[] { 1, 2, 3 }, "image/png")));
        var file = files.GetFileInfo("/assets/cms/rank.png");
        Assert.True(file.Exists);
        Assert.Equal("rank.png", file.Name);
        Assert.Equal(3, file.Length);
        using var read = new MemoryStream();
        file.CreateReadStream().CopyTo(read);
        Assert.Equal([1, 2, 3], read.ToArray());
    }

    [Fact]
    public async Task Finds_escaped_paths_and_nothing_outside_assets()
    {
        var cache = new AssetCache();
        await cache.EnsureAsync("cms/career rank/x.png", () => Task.FromResult((new byte[] { 1 }, "image/png")));
        var files = new AssetFileProvider(cache);
        Assert.True(files.GetFileInfo("assets/cms/career%20rank/x.png").Exists);
        Assert.False(files.GetFileInfo("cms/career rank/x.png").Exists);
        Assert.False(files.GetFileInfo("index.html").Exists);
        Assert.False(files.GetDirectoryContents("assets").Exists);
    }
}

public class AssetCacheTests
{
    [Fact]
    public async Task Serves_only_what_has_been_fetched()
    {
        var cache = new AssetCache();
        Assert.False(cache.TryGet("cms/rank.png", out _));
        var url = await cache.EnsureAsync("cms/rank.png", () => Task.FromResult((new byte[] { 1, 2 }, "image/png")));
        Assert.Equal("assets/cms/rank.png", url);
        Assert.True(cache.TryGet("cms/rank.png", out var a));
        Assert.Equal("image/png", a.ContentType);
        Assert.Equal([1, 2], a.Bytes);
    }

    [Fact]
    public async Task Fetches_once_and_forgets_failures()
    {
        var cache = new AssetCache();
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => cache.EnsureAsync("k", () => throw new HttpRequestException("Flood")));
        Assert.False(cache.TryGet("k", out _));
        Task<(byte[], string)> Fetch() { calls++; return Task.FromResult((Array.Empty<byte>(), "image/png")); }
        await cache.EnsureAsync("k", Fetch);
        await cache.EnsureAsync("k", Fetch);
        Assert.Equal(1, calls);
    }
}
