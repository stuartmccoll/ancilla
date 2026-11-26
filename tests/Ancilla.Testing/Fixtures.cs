using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Ancilla.Testing;

/// <summary>
/// One hand-written response per endpoint in <see cref="Ancilla.Core.ApiContract"/>, in the shape the
/// real services answer with (field names, nesting, localised strings, extra fields Ancilla doesn't
/// read) and made-up Halo values. Never a recorded response. XUIDs are written as placeholders
/// (<c>{xuid:NobleSix}</c>) and filled in from <see cref="Spartans"/>, so no decimal XUID is committed.
/// </summary>
public static partial class Fixtures
{
    private const string Prefix = "Fixtures/";

    private static readonly Dictionary<string, Spartans.Spartan> Players = new()
    {
        ["NobleSix"] = Spartans.NobleSix,
        ["Kat"] = Spartans.Kat,
        ["Jorge"] = Spartans.Jorge,
    };

    /// <summary>Every fixture's file name.</summary>
    public static IReadOnlyList<string> Names { get; } = typeof(Fixtures).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Select(n => n[Prefix.Length..]).Order().ToList();

    public static byte[] Bytes(string name)
    {
        using var stream = typeof(Fixtures).Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new FileNotFoundException($"No fixture called {name}.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The fixture as written, placeholders and all.</summary>
    public static string Raw(string name) => Encoding.UTF8.GetString(Bytes(name));

    /// <summary>The fixture with its XUID placeholders filled in.</summary>
    public static string Json(string name) =>
        Placeholder().Replace(Raw(name), m => Players.TryGetValue(m.Groups[1].Value, out var s)
            ? s.Xuid : throw new InvalidOperationException($"{name}: no Spartan called {m.Groups[1].Value}."));

    /// <summary>A handler that answers every request with one fixture, as the real service would.</summary>
    public static HttpMessageHandler Serving(string name) => new Handler(name);

    private sealed class Handler(string name) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpContent content = name.EndsWith(".json", StringComparison.Ordinal)
                ? new StringContent(Json(name), Encoding.UTF8, "application/json")
                : new ByteArrayContent(Bytes(name)) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [GeneratedRegex(@"\{xuid:([A-Za-z]+)\}")]
    private static partial Regex Placeholder();
}
