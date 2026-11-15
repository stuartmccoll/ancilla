using System.Net;
using Ancilla.Core;
using Ancilla.Theater.Format;
using static Ancilla.Theater.Tests.Synthetic;

namespace Ancilla.Theater.Tests;

public class FilmSourceTests
{
    private static readonly string MatchId = Guid.NewGuid().ToString();

    [Fact]
    public void Parses_a_manifest()
    {
        var m = FilmSource.ParseManifest(ManifestJson(MatchId, filmLengthMs: 123_456));
        Assert.Equal(123_456, m.CustomData.FilmLength);
        Assert.Equal(41, m.CustomData.FilmMajorVersion);
        Assert.Equal([1, 3], m.CustomData.Chunks.Select(c => c.ChunkType));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public void A_bad_manifest_is_invalid_data(string raw) =>
        Assert.Throws<InvalidDataException>(() => FilmSource.ParseManifest(raw));

    [Fact]
    public void Chunk_urls_join_the_blob_prefix_and_path()
    {
        var m = FilmSource.ParseManifest(ManifestJson(MatchId));
        Assert.Equal("https://blobs-infiniteugc.svc.halowaypoint.com/ugcstorage/film/abc/def/filmChunk1",
            FilmSource.ChunkUri(m, m.CustomData.Chunks[1]).ToString());
    }

    [Theory]
    [InlineData("http://blobs-infiniteugc.svc.halowaypoint.com/x/", "/filmChunk1")]   // not HTTPS
    [InlineData("https://example.com/x/", "/filmChunk1")]                            // not Halo's blob host
    [InlineData("not a url", "/filmChunk1")]
    [InlineData("https://blobs-infiniteugc.svc.halowaypoint.com/x/", "/../secret")]   // not a chunk path
    [InlineData("https://blobs-infiniteugc.svc.halowaypoint.com/x/", "https://example.com/filmChunk1")]
    public void Chunk_urls_outside_halos_blob_storage_are_refused(string prefix, string path)
    {
        var m = FilmSource.ParseManifest(ManifestJson(MatchId)) with { BlobStoragePathPrefix = prefix };
        var chunk = m.CustomData.Chunks[1] with { FileRelativePath = path };
        Assert.Throws<InvalidDataException>(() => FilmSource.ChunkUri(m, chunk));
    }

    [Fact]
    public async Task Chunks_download_without_the_token()
    {
        var handler = new FakeHandler(body: "chunk bytes");
        var source = new FilmSource(new HttpClient(handler));
        var m = FilmSource.ParseManifest(ManifestJson(MatchId));

        var bytes = await source.GetChunkAsync(m, m.CustomData.Chunks[0]);
        Assert.Equal("chunk bytes"u8.ToArray(), bytes);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("blobs-infiniteugc.svc.halowaypoint.com", req.RequestUri!.Host);
        Assert.False(req.Headers.Contains("x-343-authorization-spartan"));
    }

    [Fact]
    public async Task A_failed_chunk_download_is_an_http_error()
    {
        var source = new FilmSource(new HttpClient(new FakeHandler(HttpStatusCode.NotFound)));
        var m = FilmSource.ParseManifest(ManifestJson(MatchId));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetChunkAsync(m, m.CustomData.Chunks[0]));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }
}

public class FilmManifestClientTests
{
    private const string Token = "v4=not-a-real-token";

    [Fact]
    public async Task Fetches_the_spectate_manifest_with_the_token()
    {
        var handler = new FakeHandler(body: "{\"manifest\":true}");
        var client = new HaloWaypointClient(new HttpClient(handler), Token);
        var id = Guid.NewGuid().ToString();

        Assert.Equal("{\"manifest\":true}", await client.GetFilmManifestJsonAsync(id));
        var req = Assert.Single(handler.Requests);
        Assert.Equal($"https://discovery-infiniteugc.svc.halowaypoint.com/hi/films/matches/{id}/spectate", req.RequestUri!.ToString());
        Assert.Equal(Token, Assert.Single(req.Headers.GetValues("x-343-authorization-spartan")));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("../../users/me")]
    public async Task Refuses_a_match_id_that_is_not_a_guid(string id)
    {
        var handler = new FakeHandler();
        var client = new HaloWaypointClient(new HttpClient(handler), Token);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetFilmManifestJsonAsync(id));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_error_response_does_not_reveal_the_token()
    {
        var client = new HaloWaypointClient(new HttpClient(new FakeHandler(HttpStatusCode.Unauthorized)), Token);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetFilmManifestJsonAsync(Guid.NewGuid().ToString()));
        Assert.DoesNotContain("not-a-real-token", ex.Message);
    }
}

public class FilmCacheTests
{
    [Fact]
    public void Saves_and_loads_a_film()
    {
        using var t = new TempCache();
        var id = Guid.NewGuid().ToString();
        var board = Board(id, [Human(Xuid(1), 0, new(Kills: 3))], (0, 3));
        WriteFilm(t.Cache, id, [new HeaderPlayer(Xuid(1), "Test Alpha")], [Kill("Test Alpha", 1)], board);

        var m = t.Cache.LoadManifest(id);
        Assert.Equal(41, m.CustomData.FilmMajorVersion);
        Assert.Equal(3, t.Cache.LoadScoreboard(id)!.Players[0].Stats!.CoreStats.Kills);
        var header = t.Cache.LoadChunk(id, FilmCache.ChunkOfType(m, 1));
        Assert.Equal(41u, FilmHeader.Parse(header).Version);
        Assert.Equal([id], t.Cache.CachedMatches());
    }

    [Fact]
    public void HasChunk_needs_the_manifest_size()
    {
        using var t = new TempCache();
        var id = Guid.NewGuid().ToString();
        var chunk = new FilmChunkInfo(0, 0, 0, ChunkSize: 3, "/filmChunk0", 1);
        Assert.False(t.Cache.HasChunk(id, chunk));
        t.Cache.SaveChunk(id, chunk, [1, 2]);
        Assert.False(t.Cache.HasChunk(id, chunk));
        t.Cache.SaveChunk(id, chunk, [1, 2, 3]);
        Assert.True(t.Cache.HasChunk(id, chunk));
    }

    [Fact]
    public void ChunkOfType_needs_exactly_one_chunk_of_that_type()
    {
        var m = FilmSource.ParseManifest(ManifestJson(Guid.NewGuid().ToString()));
        Assert.Equal(1, FilmCache.ChunkOfType(m, 3).Index);
        Assert.Throws<InvalidDataException>(() => FilmCache.ChunkOfType(m, 2));
        var twoHeaders = m with { CustomData = m.CustomData with { Chunks = [.. m.CustomData.Chunks, m.CustomData.Chunks[0]] } };
        Assert.Throws<InvalidDataException>(() => FilmCache.ChunkOfType(twoHeaders, 1));
    }

    [Fact]
    public void The_newest_film_is_the_last_fetched_not_the_last_written_folder()
    {
        using var t = new TempCache();
        string older = Guid.NewGuid().ToString(), newer = Guid.NewGuid().ToString();
        t.Cache.SaveManifest(older, ManifestJson(older));
        t.Cache.SaveManifest(newer, ManifestJson(newer));
        File.SetLastWriteTimeUtc(Path.Combine(t.Cache.MatchDir(older), "manifest.json"), DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(Path.Combine(t.Cache.MatchDir(newer), "manifest.json"), DateTime.UtcNow.AddHours(-1));

        // A dump into the older film's folder makes that folder the most recently written.
        File.WriteAllBytes(Path.Combine(t.Cache.MatchDir(older), "chunk000-type1.bin"), [0]);

        Assert.Equal(newer, t.Cache.NewestMatch());
    }

    [Fact]
    public void There_is_no_newest_film_in_an_empty_cache()
    {
        using var t = new TempCache();
        Assert.Null(t.Cache.NewestMatch());
        Assert.Empty(t.Cache.CachedMatches());
    }

    [Fact]
    public void Folders_that_are_not_match_ids_are_ignored()
    {
        using var t = new TempCache();
        Directory.CreateDirectory(Path.Combine(t.Root, "not-a-match"));
        Assert.Empty(t.Cache.CachedMatches());
    }

    [Fact]
    public void Match_folders_must_be_guids()
    {
        using var t = new TempCache();
        Assert.Throws<ArgumentException>(() => t.Cache.MatchDir("../escape"));
    }

    [Fact]
    public void A_corrupt_manifest_is_invalid_data()
    {
        using var t = new TempCache();
        var id = Guid.NewGuid().ToString();
        t.Cache.SaveManifest(id, "{ broken");
        Assert.Throws<InvalidDataException>(() => t.Cache.LoadManifest(id));
    }

    [Fact]
    public void Medal_metadata_round_trips_and_is_absent_until_saved()
    {
        using var t = new TempCache();
        Assert.Null(t.Cache.LoadMedals());
        t.Cache.SaveMedals(new MedalMetadata([], [], [], [new MedalDefinition(622331684L, "Double Kill", null, 0, 0, 1, 0, 50)]));
        var medal = Assert.Single(t.Cache.LoadMedals()!.Medals);
        Assert.Equal(622331684L, medal.NameId);
        Assert.Equal(1, medal.DifficultyIndex);
    }
}

public class HomeRelativeTests
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void The_home_folder_itself_is_a_tilde() => Assert.Equal("~", SnapshotStore.HomeRelative(Home));

    [Fact]
    public void A_path_under_home_starts_with_a_tilde() =>
        Assert.Equal(Path.Combine("~", "Library", "films"), SnapshotStore.HomeRelative(Path.Combine(Home, "Library", "films")));

    [Fact]
    public void A_path_outside_home_is_unchanged()
    {
        var outside = Path.Combine(Path.GetPathRoot(Home)!, "not-home-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(outside, SnapshotStore.HomeRelative(outside));
    }

    [Fact]
    public void A_folder_that_only_starts_like_home_is_not_under_it()
    {
        var lookalike = Home + "-other";
        Assert.Equal(lookalike, SnapshotStore.HomeRelative(lookalike));
    }
}
