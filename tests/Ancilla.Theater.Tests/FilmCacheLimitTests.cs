using Ancilla.Theater;

namespace Ancilla.Theater.Tests;

public sealed class FilmCacheLimitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ancilla-films-" + Guid.NewGuid().ToString("N"));
    private readonly FilmCache _cache;

    public FilmCacheLimitTests() => _cache = new FilmCache(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Film(int n, int bytes, DateTime written)
    {
        var id = $"000ba771-0000-4000-8000-{n:D12}";
        var dir = _cache.MatchDir(id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "chunk000-type1.zlib"), new byte[bytes]);
        Directory.SetLastWriteTimeUtc(dir, written);
        return id;
    }

    [Fact]
    public void An_empty_cache_has_nothing_to_count_or_clear()
    {
        Assert.Equal(0, _cache.TotalBytes());
        Assert.Equal(0, _cache.Clear());
        Assert.Equal(0, _cache.Trim(10));
    }

    [Fact]
    public void Total_bytes_adds_up_every_film()
    {
        Film(1, 100, DateTime.UtcNow);
        Film(2, 250, DateTime.UtcNow);
        Assert.Equal(350, _cache.TotalBytes());
    }

    [Fact]
    public void Trimming_removes_the_oldest_films_first()
    {
        var now = DateTime.UtcNow;
        var oldest = Film(1, 100, now.AddDays(-3));
        var middle = Film(2, 100, now.AddDays(-2));
        var newest = Film(3, 100, now.AddDays(-1));

        Assert.Equal(1, _cache.Trim(250));
        Assert.DoesNotContain(oldest, _cache.CachedMatches());
        Assert.Contains(middle, _cache.CachedMatches());
        Assert.Contains(newest, _cache.CachedMatches());
        Assert.Equal(0, _cache.Trim(250));   // already within the limit
    }

    [Fact]
    public void Trimming_never_removes_the_film_just_downloaded()
    {
        var now = DateTime.UtcNow;
        var justFetched = Film(1, 500, now.AddDays(-9));   // oldest by time, but protected
        var other = Film(2, 100, now.AddDays(-1));

        _cache.Trim(50, keep: justFetched);
        Assert.Equal([justFetched], _cache.CachedMatches());
        Assert.DoesNotContain(other, _cache.CachedMatches());
    }

    [Fact]
    public void Clearing_deletes_every_film_but_not_other_files()
    {
        Film(1, 10, DateTime.UtcNow);
        Film(2, 10, DateTime.UtcNow);
        File.WriteAllText(_cache.MedalsPath, "{}");
        Assert.Equal(2, _cache.Clear());
        Assert.Empty(_cache.CachedMatches());
        Assert.True(File.Exists(_cache.MedalsPath));
    }
}
