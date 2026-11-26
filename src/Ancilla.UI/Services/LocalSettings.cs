using System.Text.Json;
using Ancilla.Core;

namespace Ancilla.UI.Services;

/// <summary>
/// Choices made on the Settings page that both hosts keep the same way: a small JSON file next to
/// the archive. Only written once something is changed from its default. (The theme and "Stay
/// signed in" are kept by each host, see <see cref="IThemePreference"/> and <see cref="IWaypointSignIn"/>.)
/// </summary>
public sealed class LocalSettings(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();
    private Values? _values;

    private sealed record Values(int? FilmCacheLimitMb = null);

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(SnapshotStore.DefaultPath)!, "settings.json");

    public string FilePath => path;
    public string DisplayPath => SnapshotStore.HomeRelative(path);

    /// <summary>The film cache's size limit picked on the Settings page, or null for the default.</summary>
    public int? FilmCacheLimitMb
    {
        get => Load().FilmCacheLimitMb;
        set
        {
            if (value is <= 0) throw new ArgumentOutOfRangeException(nameof(value), "The limit must be positive.");
            Save(Load() with { FilmCacheLimitMb = value });
        }
    }

    /// <summary>Deletes the file, so every choice goes back to its default.</summary>
    public void Erase()
    {
        lock (_gate)
        {
            _values = new();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private Values Load()
    {
        lock (_gate)
        {
            if (_values is not null) return _values;
            try
            {
                _values = File.Exists(path) ? JsonSerializer.Deserialize<Values>(File.ReadAllText(path), Json) ?? new() : new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _values = new();   // a damaged file means the defaults; the next change rewrites it
            }
            if (_values.FilmCacheLimitMb is <= 0) _values = _values with { FilmCacheLimitMb = null };
            return _values;
        }
    }

    private void Save(Values values)
    {
        lock (_gate)
        {
            _values = values;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(values, Json));
        }
    }
}
