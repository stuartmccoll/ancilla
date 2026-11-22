using System.Collections.Concurrent;

namespace Ancilla.UI.Services;

/// <summary>
/// Holds Halo CMS images (medal sprite sheets, rank icons) that need the Spartan token to
/// download. A signed-in session fetches them once with its token; the browser then loads
/// them from <c>/assets/{key}</c>, which serves only what's already cached and never needs a token.
/// </summary>
public sealed class AssetCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<(byte[] Bytes, string ContentType)>>> _items = new();

    /// <summary>How many images are held, and their total size in bytes.</summary>
    public (int Count, long Bytes) Size()
    {
        int n = 0; long bytes = 0;
        foreach (var lazy in _items.Values)
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully) { n++; bytes += lazy.Value.Result.Bytes.Length; }
        return (n, bytes);
    }

    /// <returns>The relative URL the page should use.</returns>
    public async Task<string> EnsureAsync(string key, Func<Task<(byte[] Bytes, string ContentType)>> fetch)
    {
        var lazy = _items.GetOrAdd(key, _ => new(fetch));
        try
        {
            await lazy.Value;
        }
        catch
        {
            _items.TryRemove(new(key, lazy));
            throw;
        }
        return "assets/" + key;
    }

    public bool TryGet(string key, out (byte[] Bytes, string ContentType) asset)
    {
        if (_items.TryGetValue(key, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
        {
            asset = lazy.Value.Result;
            return true;
        }
        asset = default;
        return false;
    }
}
