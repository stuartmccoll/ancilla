using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Ancilla.Core;
using Ancilla.Theater.Format;

namespace Ancilla.Theater.Tests;

/// <summary>
/// Builds films from scratch, bit by bit, so tests never need a real film. Real films hold other
/// players' gamertags and XUIDs and must never be committed. Every name and XUID here is made up.
/// </summary>
public static class Synthetic
{
    /// <summary>Made-up XUIDs in the Xbox Live range (0x0009_xxxx_xxxx_xxxx).</summary>
    public static ulong Xuid(int n) => 0x0009_0000_0000_0000UL + 0x100UL + (ulong)n;

    public const int LongRecordOffset = 12_342;   // XUID to first name copy, long header records
    public const int ShortRecordOffset = 936;     // ... and short ones
    public const int CopyGap = 15_300;            // first to second name copy

    /// <summary>One player's header record: XUID, then two copies of the name.</summary>
    public sealed record HeaderPlayer(ulong Xuid, string Name, int FirstCopyOffset = LongRecordOffset, byte[]? FirstCopyField = null);

    /// <summary>
    /// A header chunk: version 41, one registry block with one component, a 3-value table, the
    /// build strings, then a bit stream with each player's record about 28,000 bits apart.
    /// </summary>
    public static byte[] Header(IReadOnlyList<HeaderPlayer> players) => Header(players, out _);

    public static byte[] Header(IReadOnlyList<HeaderPlayer> players, out int streamOffset)
    {
        var bytes = new List<byte>();
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); bytes.AddRange(b); }
        void Text(string s, int size) { var b = new byte[size]; Encoding.ASCII.GetBytes(s, b); bytes.AddRange(b); }

        U32(41);
        U32(1);
        var block = new byte[FilmHeader.BlockSize];
        Encoding.ASCII.GetBytes("object-position-component", block);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(256), 1);
        bytes.AddRange(block);
        U32(1); U32(2); U32(3);
        Text("6.10026.19225.0", 32); Text("HI_1_13_0", 32); Text("release", 32);
        U32(269225); U32(8844991);
        streamOffset = bytes.Count;

        var w = new BitWriter();
        foreach (var p in players)
        {
            var start = w.Length;
            w.Write(p.Xuid, 64);
            w.ZerosTo(start + p.FirstCopyOffset);
            w.Bytes(p.FirstCopyField ?? Roster.NameField(p.Name));
            w.ZerosTo(start + p.FirstCopyOffset + CopyGap);
            w.Bytes(Roster.NameField(p.Name));
            w.ZerosTo(start + 28_000);
        }
        bytes.AddRange(w.ToArray());
        return bytes.ToArray();
    }

    /// <summary>One summary record's contents. Code 10 = death, 25 = player kill.</summary>
    public sealed record Event(string Name, int Code, uint TimeMs, bool Medal = false, int MedalCode = 0,
        int BodyOffset = LongRecordOffset, uint Separator = SummaryRecords.Separator);

    public static Event Kill(string name, uint t) => new(name, Summary.PlayerKillCode, t);
    public static Event Death(string name, uint t) => new(name, Summary.DeathCode, t);
    public static Event Medal(string name, uint t, int medalCode, int tier = 50) => new(name, tier, t, Medal: true, MedalCode: medalCode);

    /// <summary>
    /// A summary type-9 payload: u32 record count, then per record a zero body (header offset +
    /// 15,030 bits), the name field, the event bits and the u32 separator.
    /// </summary>
    public static byte[] SummaryPayload(IReadOnlyList<Event> events, uint? storedCount = null)
    {
        var w = new BitWriter();
        w.Write(storedCount ?? (uint)events.Count, 32);
        foreach (var e in events)
        {
            w.Zeros(e.BodyOffset + SummaryRecords.BodyAfterHeaderOffset);
            var name = w.Length;
            w.Bytes(Roster.NameField(e.Name));
            w.ZerosTo(name + Summary.CodeOffset);
            w.Write((ulong)e.Code, Summary.CodeBits);
            w.ZerosTo(name + Summary.TimeOffset);
            w.Write(e.TimeMs, Summary.TimeBits);
            w.ZerosTo(name + Summary.MedalFlagOffset);
            w.Write(e.Medal ? 1UL : 0UL, 1);
            w.ZerosTo(name + Summary.MedalCodeOffset);
            w.Write((ulong)e.MedalCode, Summary.MedalCodeBits);
            w.Write(e.Separator, SummaryRecords.SeparatorBits);
        }
        return w.ToArray();
    }

    /// <summary>A packet: u16 type, u16 field 2, u32 length, u64 timestamp, payload.</summary>
    public static byte[] Packet(ushort type, ulong timestampUs, byte[]? payload = null, ushort field2 = 0)
    {
        payload ??= [];
        var b = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, type);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), field2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), timestampUs);
        payload.CopyTo(b, 16);
        return b;
    }

    public static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Fastest)) z.Write(data);
        return output.ToArray();
    }

    // ---- Scoreboards ----

    public sealed record PlayerStats(int Kills = 0, int Deaths = 0, int Medals = 0, int Suicides = 0);

    public static MatchPlayer Human(ulong xuid, int team, PlayerStats s, bool joinedLate = false, long medalId = 622331684L) =>
        Player($"xuid({xuid})", 1, team, s, joinedLate, medalId);

    public static MatchPlayer Bot(int n, int team, PlayerStats s) => Player($"bid({n}.0)", 2, team, s, joinedLate: true, 622331684L);

    private static MatchPlayer Player(string id, int type, int team, PlayerStats s, bool joinedLate, long medalId) =>
        new(id, type, team, MatchOutcome.Win, 1,
            new ParticipationInfo(joinedLate, false, true, "PT5M"),
            [new PlayerTeamStats(team, Block(s, medalId))]);

    public static StatsBlock Block(PlayerStats s, long medalId = 622331684L) => new()
    {
        CoreStats = new CoreStats
        {
            Kills = s.Kills, Deaths = s.Deaths, Suicides = s.Suicides, Spawns = s.Deaths + 1,
            MaxKillingSpree = s.Kills, AverageLifeDuration = "PT30S",
            Medals = s.Medals == 0 ? [] : [new MedalCount(medalId, s.Medals, 0)],
        },
        ModeStats = { ["PvpStats"] = JsonDocument.Parse($"{{\"Kills\":{s.Kills}}}").RootElement.Clone() },
    };

    public static MatchStats Board(string matchId, IReadOnlyList<MatchPlayer> players, params (int Team, int Kills)[] teams) =>
        new(matchId,
            new MatchInfo(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5), null, "PT5M", 9),
            teams.Select(t => new MatchTeam(t.Team, MatchOutcome.Win, 1,
                new StatsBlock { CoreStats = new CoreStats { Kills = t.Kills, Score = 1 } })).ToList(),
            players.ToList());

    /// <summary>A manifest's raw JSON with one header chunk and one summary chunk.</summary>
    public static string ManifestJson(string matchId, long filmLengthMs = 300_000) => $$"""
        {
          "FilmStatusBond": 1,
          "CustomData": {
            "FilmLength": {{filmLengthMs}},
            "Chunks": [
              { "Index": 0, "ChunkStartTimeOffsetMilliseconds": 0, "DurationMilliseconds": 0, "ChunkSize": 0, "FileRelativePath": "/filmChunk0", "ChunkType": 1 },
              { "Index": 1, "ChunkStartTimeOffsetMilliseconds": 0, "DurationMilliseconds": 0, "ChunkSize": 0, "FileRelativePath": "/filmChunk1", "ChunkType": 3 }
            ],
            "HasGameEnded": true,
            "MatchId": "{{matchId}}",
            "FilmMajorVersion": 41
          },
          "BlobStoragePathPrefix": "https://blobs-infiniteugc.svc.halowaypoint.com/ugcstorage/film/abc/def/",
          "AssetId": "{{Guid.Empty}}"
        }
        """;

    /// <summary>Writes a complete film (manifest, scoreboard, header and summary chunks) into a cache.</summary>
    public static void WriteFilm(FilmCache cache, string matchId, IReadOnlyList<HeaderPlayer> roster,
        IReadOnlyList<Event> events, MatchStats board)
    {
        var raw = ManifestJson(matchId);
        cache.SaveManifest(matchId, raw);
        cache.SaveScoreboard(matchId, board);
        var manifest = FilmSource.ParseManifest(raw);
        cache.SaveChunk(matchId, FilmCache.ChunkOfType(manifest, 1), Zlib(Header(roster)));
        var summary = Packet(9, 1_000, SummaryPayload(events)).Concat(Packet(7, 2_000)).ToArray();
        cache.SaveChunk(matchId, FilmCache.ChunkOfType(manifest, 3), Zlib(summary));
    }
}

/// <summary>Writes bits most-significant first, the order films use.</summary>
public sealed class BitWriter
{
    private readonly List<bool> _bits = [];
    public long Length => _bits.Count;

    public BitWriter Write(ulong value, int bits)
    {
        for (var i = bits - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) == 1);
        return this;
    }

    public BitWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) Write(b, 8);
        return this;
    }

    public BitWriter Zeros(long count)
    {
        for (var i = 0L; i < count; i++) _bits.Add(false);
        return this;
    }

    public BitWriter ZerosTo(long position)
    {
        if (position < Length) throw new InvalidOperationException($"Already at bit {Length}, past {position}.");
        return Zeros(position - Length);
    }

    public byte[] ToArray()
    {
        var bytes = new byte[(_bits.Count + 7) / 8];
        for (var i = 0; i < _bits.Count; i++)
            if (_bits[i]) bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        return bytes;
    }
}

/// <summary>An HTTP handler that records requests and answers with a fixed response.</summary>
public sealed class FakeHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "{}") : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}

/// <summary>A temporary film cache folder, deleted afterwards.</summary>
public sealed class TempCache : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ancilla-theater-tests-" + Guid.NewGuid().ToString("N"));
    public FilmCache Cache { get; }
    public TempCache() => Cache = new FilmCache(Root);
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}
