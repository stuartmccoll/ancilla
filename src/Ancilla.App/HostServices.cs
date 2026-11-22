using Ancilla.Core;
using Ancilla.UI.Services;

namespace Ancilla.App;

/// <summary>The theme is kept in the app's own settings, the desktop's version of the web host's cookie.</summary>
public sealed class SettingsThemePreference : IThemePreference
{
    private const string Key = "theme";

    public string? Saved => Preferences.Default.Get<string?>(Key, null);

    public void Save(string id) => Preferences.Default.Set(Key, id);
}

/// <summary>Exports go straight to the Downloads folder, as a browser download would.</summary>
public sealed class DownloadsFileExport : IFileExport
{
    public async Task<string?> SaveAsync(string fileName, string mime, string text)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        // Don't overwrite an earlier export: "ancilla-matches (2).csv", like a browser.
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}");
        await File.WriteAllTextAsync(path, text);
        return SnapshotStore.HomeRelative(path);
    }
}
