using System.IO.Compression;
using System.Text.Json;
using Ancilla.Core;

namespace Ancilla.Theater;

/// <summary>
/// Films on disk, outside the repo: {LocalApplicationData}/Ancilla/films/{matchId}/. Films hold
/// every player's gamertag and XUID, so they must never be committed.
/// </summary>
public sealed class FilmCache(string root)
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ancilla", "films");

    public string Root => root;

    /// <summary><see cref="Root"/> with the home folder shown as ~, so output doesn't show the account name.</summary>
    public string DisplayRoot => SnapshotStore.HomeRelative(root);

    public string MatchDir(string matchId) => Path.Combine(root, HaloIds.RequireGuid(matchId, nameof(matchId)));

    public IEnumerable<string> CachedMatches() =>
        Directory.Exists(root)
            ? Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>().Where(HaloIds.IsGuid)
            : [];

    /// <summary>
    /// The most recently fetched film: the one whose manifest was written last. (The folder's own
    /// time changes whenever anything is written into it, such as a <c>dump</c>.)
    /// </summary>
    public string? NewestMatch() =>
        CachedMatches()
            .Select(id => (id, path: Path.Combine(MatchDir(id), "manifest.json")))
            .Where(x => File.Exists(x.path))
            .OrderByDescending(x => File.GetLastWriteTimeUtc(x.path))
            .Select(x => x.id)
            .FirstOrDefault();

    public void SaveManifest(string matchId, string rawJson) => Write(matchId, "manifest.json", rawJson);

    /// <summary>Medal metadata (names and difficulties): the same for everyone, so one copy for all films.</summary>
    public string MedalsPath => Path.Combine(root, "medals.json");
    public void SaveMedals(MedalMetadata medals)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(MedalsPath, JsonSerializer.Serialize(medals, FilmSource.Json));
    }
    public MedalMetadata? LoadMedals() =>
        File.Exists(MedalsPath) ? JsonSerializer.Deserialize<MedalMetadata>(File.ReadAllText(MedalsPath), FilmSource.Json) : null;
    public void SaveScoreboard(string matchId, MatchStats stats) =>
        Write(matchId, "scoreboard.json", JsonSerializer.Serialize(stats, FilmSource.Json));

    public void SaveChunk(string matchId, FilmChunkInfo c, byte[] compressed)
    {
        Directory.CreateDirectory(MatchDir(matchId));
        File.WriteAllBytes(ChunkPath(matchId, c), compressed);
    }

    public bool HasChunk(string matchId, FilmChunkInfo c) =>
        File.Exists(ChunkPath(matchId, c)) && new FileInfo(ChunkPath(matchId, c)).Length == c.ChunkSize;

    /// <summary>Whether a match's manifest and every chunk it lists are cached.</summary>
    public bool HasFilm(string matchId)
    {
        if (!File.Exists(Path.Combine(MatchDir(matchId), "manifest.json"))) return false;
        try { return LoadManifest(matchId).CustomData.Chunks.All(c => HasChunk(matchId, c)); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { return false; }
    }

    public FilmManifest LoadManifest(string matchId) =>
        FilmSource.ParseManifest(File.ReadAllText(Path.Combine(MatchDir(matchId), "manifest.json")));

    public MatchStats? LoadScoreboard(string matchId)
    {
        var path = Path.Combine(MatchDir(matchId), "scoreboard.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<MatchStats>(File.ReadAllText(path), FilmSource.Json) : null;
    }

    /// <summary>The film's only chunk of <paramref name="chunkType"/> (1 = header, 3 = summary).</summary>
    public static FilmChunkInfo ChunkOfType(FilmManifest manifest, int chunkType)
    {
        var found = manifest.CustomData.Chunks.Where(c => c.ChunkType == chunkType).ToList();
        return found.Count == 1 ? found[0]
            : throw new InvalidDataException($"The film has {found.Count} chunks of type {chunkType}; expected exactly 1.");
    }

    /// <summary>The chunk, zlib-decompressed.</summary>
    public byte[] LoadChunk(string matchId, FilmChunkInfo c)
    {
        using var input = File.OpenRead(ChunkPath(matchId, c));
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private string ChunkPath(string matchId, FilmChunkInfo c) =>
        Path.Combine(MatchDir(matchId), $"chunk{c.Index:D3}-type{c.ChunkType}.zlib");

    private void Write(string matchId, string name, string text)
    {
        Directory.CreateDirectory(MatchDir(matchId));
        File.WriteAllText(Path.Combine(MatchDir(matchId), name), text);
    }
}
