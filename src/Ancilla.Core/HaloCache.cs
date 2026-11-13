using System.Collections.Concurrent;

namespace Ancilla.Core;

/// <summary>
/// Thread-safe, in-memory async cache for data that never changes once published: finished
/// matches, versioned maps/modes/playlists, medal metadata, track definitions. Concurrent
/// callers for the same key share one request. Failures are not cached.
/// </summary>
public sealed class HaloCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _items = new();

    public async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory)
    {
        var lazy = _items.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () => await factory()));
        try
        {
            return (T)(await lazy.Value)!;
        }
        catch
        {
            _items.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(key, lazy));
            throw;
        }
    }
}
