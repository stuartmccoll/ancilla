using System.Text.Json;
using Ancilla.Core;

namespace Ancilla.Theater;

/// <summary>GET discovery-infiniteugc.svc.halowaypoint.com/hi/films/matches/{matchId}/spectate</summary>
public sealed record FilmManifest(int FilmStatusBond, FilmCustomData CustomData, string BlobStoragePathPrefix, string AssetId);

public sealed record FilmCustomData(
    long FilmLength, List<FilmChunkInfo> Chunks, bool HasGameEnded, string MatchId, int FilmMajorVersion);

/// <summary>ChunkType 1 = header, 2 = replication (about 20 s each), 3 = summary.</summary>
public sealed record FilmChunkInfo(
    int Index, long ChunkStartTimeOffsetMilliseconds, long DurationMilliseconds, long ChunkSize,
    string FileRelativePath, int ChunkType);

/// <summary>
/// Film manifests and chunks. The manifest needs the Spartan token, so it's fetched by
/// <see cref="HaloWaypointClient.GetFilmManifestJsonAsync"/>, the only holder of the token. Chunks
/// are public blobs, so this class downloads them with a plain <see cref="HttpClient"/> and never
/// sees the token.
/// </summary>
public sealed class FilmSource(HttpClient http)
{
    private const string BlobHost = "blobs-infiniteugc.svc.halowaypoint.com";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Parses a manifest's raw JSON, as returned by the spectate endpoint.</summary>
    public static FilmManifest ParseManifest(string rawJson)
    {
        try
        {
            return JsonSerializer.Deserialize<FilmManifest>(rawJson, Json) ?? throw new InvalidDataException("Empty film manifest.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Film manifest isn't valid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Where a chunk is stored. Only Halo's blob host over HTTPS and only <c>/filmChunkN</c> paths
    /// are accepted, whatever the manifest says.
    /// </summary>
    public static Uri ChunkUri(FilmManifest manifest, FilmChunkInfo chunk)
    {
        if (!Uri.TryCreate(manifest.BlobStoragePathPrefix.TrimEnd('/') + "/", UriKind.Absolute, out var prefix)
            || prefix.Scheme != Uri.UriSchemeHttps || prefix.Host != BlobHost)
            throw new InvalidDataException("Unexpected film storage host.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(chunk.FileRelativePath, @"^/filmChunk\d+$"))
            throw new InvalidDataException("Unexpected film chunk path.");
        return new Uri(prefix, chunk.FileRelativePath.TrimStart('/'));
    }

    public async Task<byte[]> GetChunkAsync(FilmManifest manifest, FilmChunkInfo chunk, CancellationToken ct = default)
    {
        using var res = await http.GetAsync(ChunkUri(manifest, chunk), ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Film chunk {chunk.Index} failed with HTTP {(int)res.StatusCode}.", null, res.StatusCode);
        return await res.Content.ReadAsByteArrayAsync(ct);
    }
}
