using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ancilla.Core;

namespace SignInSpike;

/// <summary>
/// Facts about a token that are safe to show: its shape, its JOSE header's algorithm fields, and a
/// short hash so two reads can be told apart. Never the token, and never any claim but exp/iat.
/// </summary>
public sealed record TokenFormat(
    int Length,
    bool WasUrlEncoded,
    bool HadV4Prefix,
    int Segments,
    string Kind,
    string? Header,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? ExpiresClaim,
    string Fingerprint)
{
    public static TokenFormat Of(string raw)
    {
        var trimmed = raw.Trim().Trim('"');
        var normalised = HaloWaypointClient.NormaliseToken(trimmed);
        var body = normalised["v4=".Length..];
        var parts = body.Split('.');

        string kind = parts.Length switch { 5 => "JWE (encrypted)", 3 => "JWS (signed)", _ => "unknown" };
        string? header = null;
        DateTimeOffset? iat = null, exp = null;

        if (parts.Length is 3 or 5 && TryJson(parts[0]) is { } h)
            header = string.Join(", ", new[] { "alg", "enc", "typ", "zip" }
                .Where(k => h.TryGetProperty(k, out _))
                .Select(k => $"{k}={h.GetProperty(k)}"));

        // A signed token's payload is readable: pick out the two time claims and nothing else.
        if (parts.Length == 3 && TryJson(parts[1]) is { } p)
        {
            if (p.TryGetProperty("iat", out var i) && i.TryGetInt64(out var iv)) iat = DateTimeOffset.FromUnixTimeSeconds(iv);
            if (p.TryGetProperty("exp", out var e) && e.TryGetInt64(out var ev)) exp = DateTimeOffset.FromUnixTimeSeconds(ev);
        }

        return new TokenFormat(
            normalised.Length,
            WasUrlEncoded: trimmed.Contains('%'),
            HadV4Prefix: Uri.UnescapeDataString(trimmed).StartsWith("v4=", StringComparison.Ordinal),
            parts.Length, kind, header, iat, exp,
            HashOf(normalised));
    }

    public static string HashOf(string normalisedToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalisedToken)))[..8].ToLowerInvariant();

    private static JsonElement? TryJson(string base64Url)
    {
        try
        {
            var s = base64Url.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(s));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
