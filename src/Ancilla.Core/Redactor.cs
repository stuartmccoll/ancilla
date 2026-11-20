using System.Text.RegularExpressions;

namespace Ancilla.Core;

/// <summary>
/// Removes anything private from text before it's logged or copied into a bug report: Spartan
/// tokens, XUIDs (decimal, hex and <c>xuid(...)</c>), film chunk and other URLs (cut down to the
/// host), absolute paths, and any gamertags or clearance IDs registered with <see cref="Register"/>.
/// Gamertags and clearance IDs have no recognisable shape, so whoever sees one registers it.
/// </summary>
public sealed partial class Redactor
{
    private readonly object _gate = new();
    private volatile string[] _secrets = [];

    public Redactor()
    {
        // Paths under the home folder name the account, so the folder itself is always a secret.
        Register(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Register(Environment.UserName);
    }

    /// <summary>Adds a value (a gamertag, a clearance ID, a token) that must never be written out.</summary>
    public void Register(string? secret)
    {
        secret = secret?.Trim();
        if (string.IsNullOrEmpty(secret) || secret.Length < 3) return;
        lock (_gate)
        {
            if (_secrets.Contains(secret, StringComparer.OrdinalIgnoreCase)) return;
            _secrets = [.. _secrets, secret];
            Array.Sort(_secrets, (a, b) => b.Length.CompareTo(a.Length));   // longest first
        }
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;

        foreach (var secret in _secrets)
            s = s.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);

        s = Url().Replace(s, m => $"{m.Groups[1].Value}://{m.Groups[2].Value}/[url]");
        s = Token().Replace(s, "[token]");
        s = XuidCall().Replace(s, "xuid([xuid])");
        s = HexXuid().Replace(s, "[xuid]");
        s = DecimalXuid().Replace(s, "[xuid]");
        s = WindowsPath().Replace(s, "[path]");
        s = UnixPath().Replace(s, "[path]");
        return s;
    }

    [GeneratedRegex(@"\b(https?)://([^/\s""'<>?#]+)[^\s""'<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    // "v4=..." or "v4%3D..." cookie values, and bare JWE/JWT-looking strings.
    [GeneratedRegex(@"v4(?:=|%3D)[A-Za-z0-9._~%+/=-]+|eyJ[A-Za-z0-9_-]{6,}(?:\.[A-Za-z0-9_-]+)*", RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    [GeneratedRegex(@"xuid\(\s*(?!\{xuid\})[^)\s]*\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex XuidCall();

    // Real XUIDs are 0x0009_xxxx_xxxx_xxxx, with or without the 0x prefix.
    [GeneratedRegex(@"(?<![0-9A-Za-z])(?:0x)?0009[0-9A-Fa-f]{12}(?![0-9A-Za-z])")]
    private static partial Regex HexXuid();

    // The same range in decimal: 16 digits, 0x0009_0000_0000_0000 to 0x0009_FFFF_FFFF_FFFF.
    [GeneratedRegex(@"(?<!\d)2[5-8]\d{14}(?!\d)")]
    private static partial Regex DecimalXuid();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:\\[^\s""'<>|*?]+")]
    private static partial Regex WindowsPath();

    // Two or more segments, so "/health" or an endpoint name stays but "/srv/data/x" goes.
    [GeneratedRegex(@"(?<![\w.:/<>\]\)-])/[\w.~-]+(?:/[\w.~-]+)+")]
    private static partial Regex UnixPath();
}
