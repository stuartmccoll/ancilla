namespace Ancilla.Web.Services;

/// <summary>A selectable look. Swatches preview it in the picker (background, surface, accent).</summary>
public sealed record ThemeInfo(string Id, string Name, string Description, string[] Swatches);

/// <summary>
/// The theme catalogue. Each theme's colours and shapes live in wwwroot/app.css under
/// <c>:root[data-theme="id"]</c>; adding one means a CSS block plus an entry here.
/// </summary>
public static class Themes
{
    public const string CookieName = "theme";

    public static readonly IReadOnlyList<ThemeInfo> All =
    [
        new("basic", "Basic", "Clean and neutral; follows your system's light or dark setting.", ["#f3f5f7", "#161f25", "#2f7d4f"]),
        new("halo", "Halo", "Deep navy, cyan highlights, angular panels.", ["#07111a", "#0e1b27", "#46c2e0"]),
        new("xbox", "XBOX", "Original Xbox: black, glowing lime green, rounded glossy pills.", ["#040904", "#0b170a", "#8fd43f"]),
    ];

    public static ThemeInfo Default => All[0];

    /// <summary>A known theme ID, or the default for anything else (the cookie is user-controlled).</summary>
    public static string Normalize(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))?.Id ?? Default.Id;
}
