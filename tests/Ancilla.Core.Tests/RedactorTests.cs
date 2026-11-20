using Ancilla.Core;

namespace Ancilla.Core.Tests;

/// <summary>
/// Real-shaped secrets, built at run time so the privacy check never sees them in the source,
/// go through the redactor and through the log file. None may come out.
/// </summary>
public sealed class RedactorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ancilla-redact-" + Guid.NewGuid().ToString("N"));

    // A JWE-shaped token: five dot-separated base64url parts, starting with the usual header.
    private static readonly string Jwe = string.Join('.', "eyJh" + "bGciOiJkaXIiLCJlbmMiOiJBMjU2R0NNIn0", "", "Zm9vYmFyYmF6cXV4", "c2VjcmV0Y2lwaGVydGV4dA", "dGFnVGFnVGFnVGFn");
    private static readonly string Token = "v4=" + Jwe;
    private static readonly string Hex = "0009" + "00000000abcd";
    private static readonly string DecimalXuid = 0x0009_0000_0000_abcdUL.ToString();
    private static readonly string Unix = "/" + "home/someone/.local/share/Ancilla/snapshots.json";
    private static readonly string Windows = "C:" + @"\Users\someone\AppData\Local\Ancilla\films";

    private readonly Redactor _r = new();

    private static readonly string[] All =
        [Token, Uri.EscapeDataString(Token), Jwe, Hex, "0x" + Hex, DecimalXuid, Unix, Windows];

    public static TheoryData<string> Secrets()
    {
        var data = new TheoryData<string>();
        foreach (var s in All) data.Add(s);
        return data;
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Secrets_never_survive(string secret)
    {
        var text = $"before {secret} after";
        var output = _r.Redact(text);
        Assert.DoesNotContain(secret, output);
        Assert.StartsWith("before ", output);
        Assert.EndsWith(" after", output);
    }

    [Fact]
    public void Xuid_calls_and_film_urls_are_cut_down()
    {
        var o = _r.Redact($"GET https://halostats.svc.halowaypoint.com/hi/players/xuid({DecimalXuid})/matches?start=0 and https://blob.example.net/films/abc/chunk1.bin?sig=SECRET");
        Assert.DoesNotContain(DecimalXuid, o);
        Assert.DoesNotContain("SECRET", o);
        Assert.DoesNotContain("chunk1", o);
        Assert.Contains("halostats.svc.halowaypoint.com", o);
    }

    [Fact]
    public void Registered_gamertags_and_clearances_are_removed_whatever_the_case()
    {
        var clearance = Guid.NewGuid().ToString();
        _r.Register("Noble Six");
        _r.Register(clearance);
        var o = _r.Redact($"Hello noble six, flight {clearance.ToUpperInvariant()}");
        Assert.DoesNotContain("noble six", o, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(clearance, o, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Endpoint_names_and_ordinary_text_are_left_alone()
    {
        const string text = "halostats/hi/matches/{id}/stats 200 120 ms";
        Assert.Equal(text, _r.Redact(text));
    }

    [Fact]
    public void The_log_file_holds_none_of_them()
    {
        _r.Register("Noble Six");
        var log = new DiagnosticLog(_dir, _r);
        foreach (var secret in All)
            log.Write($"failed for Noble Six: {secret}");

        var text = string.Concat(Directory.EnumerateFiles(_dir).Select(File.ReadAllText));
        Assert.Contains("failed for", text);
        foreach (var secret in All)
            Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("Noble Six", text);
    }

    [Fact]
    public void The_log_rolls_when_full_and_forgets_old_files()
    {
        var log = new DiagnosticLog(_dir, _r, maxBytes: 200, keepDays: 7);
        for (var i = 0; i < 20; i++) log.Write("a line of some length to fill the file up quickly " + i);
        Assert.True(Directory.GetFiles(_dir, "ancilla-*.log").Length > 1);

        var old = Path.Combine(_dir, "ancilla-20000101.log");
        File.WriteAllText(old, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        new DiagnosticLog(_dir, _r, 200, 7).Write("prune");
        Assert.False(File.Exists(old));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
