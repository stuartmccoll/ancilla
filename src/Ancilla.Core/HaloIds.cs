using System.Globalization;

namespace Ancilla.Core;

/// <summary>
/// Format checks for IDs that end up inside Halo API URLs. Some come from the address bar
/// (match and player pages), so anything else could change which endpoint is called.
/// </summary>
public static class HaloIds
{
    /// <summary>A GUID, as used for match, playlist, asset and version IDs.</summary>
    public static bool IsGuid(string? id) => Guid.TryParseExact(id, "D", out _);

    /// <summary>An Xbox user ID: an unsigned 64-bit number in plain decimal digits.</summary>
    public static bool IsXuid(string? xuid) =>
        !string.IsNullOrEmpty(xuid) && ulong.TryParse(xuid, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    public static string RequireGuid(string? id, string name) =>
        IsGuid(id) ? id! : throw new ArgumentException($"Not a valid {name}.", name);

    public static string RequireXuid(string? xuid) =>
        IsXuid(xuid) ? xuid! : throw new ArgumentException("Not a valid player ID.", nameof(xuid));
}
