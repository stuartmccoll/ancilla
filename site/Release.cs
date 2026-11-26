using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ancilla.Site;

/// <summary>One file attached to a GitHub release.</summary>
public sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string Url,
    [property: JsonPropertyName("size")] long Size);

/// <summary>
/// A GitHub release, in the shape <c>GET /repos/{owner}/{repo}/releases/latest</c> returns (only the
/// fields the download page needs). The site workflow saves that response and passes it to the build,
/// so the download links are baked into the page: no script, and no request to GitHub's API from visitors.
/// </summary>
public sealed record Release(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("html_url")] string Url,
    [property: JsonPropertyName("published_at")] DateTimeOffset? Published,
    [property: JsonPropertyName("assets")] IReadOnlyList<ReleaseAsset> Assets)
{
    public static Release? Parse(string json)
    {
        var release = JsonSerializer.Deserialize<Release>(json);
        if (release is null || string.IsNullOrWhiteSpace(release.Tag)) return null;
        // Only GitHub's own download links: the file is ours, but nothing else should end up as a link.
        var assets = (release.Assets ?? [])
            .Where(a => Uri.TryCreate(a.Url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == "github.com")
            .ToList();
        return release with { Assets = assets };
    }

    /// <summary>Installer types, best first. Ancilla is Windows only.</summary>
    public static readonly string[] InstallerExtensions = [".msix", ".msi", ".exe", "-win-x64.zip"];

    /// <summary>What the app runs on, as the site says it.</summary>
    public const string Requirements = "Windows 10 (version 1809) or later, 64-bit";

    /// <summary>The installer: the first asset with one of <see cref="InstallerExtensions"/>, best first, then by name.</summary>
    public ReleaseAsset? Installer =>
        Assets.Select(a => (Asset: a, Rank: Array.FindIndex(InstallerExtensions, e => a.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase))))
              .Where(x => x.Rank >= 0)
              .OrderBy(x => x.Rank)
              .ThenBy(x => x.Asset.Name, StringComparer.Ordinal)
              .Select(x => x.Asset)
              .FirstOrDefault();
}
