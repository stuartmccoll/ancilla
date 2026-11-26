using Ancilla.Core;
using Ancilla.UI.Services;

namespace Ancilla.App;

/// <summary>The theme is kept in the app's own settings, the desktop's version of the web host's cookie.</summary>
public sealed class SettingsThemePreference : IThemePreference
{
    private const string Key = "theme";

    public string? Saved => Preferences.Default.Get<string?>(Key, null);

    public void Save(string id) => Preferences.Default.Set(Key, id);

    public void Forget() => Preferences.Default.Remove(Key);
}

/// <summary>
/// The system clipboard. The web view's own (<c>navigator.clipboard</c>) isn't usable here: WebKit
/// doesn't count the app's page as a secure context, and wants the write inside the click itself,
/// not after a trip to .NET and back.
/// </summary>
public sealed class SystemClipboard : IClipboardWriter
{
    public Task SetTextAsync(string text) =>
        MainThread.InvokeOnMainThreadAsync(() => Clipboard.Default.SetTextAsync(text));
}

/// <summary>Opens a folder in Finder or File Explorer, creating it first so there's something to show.</summary>
public sealed class SystemFolderOpener : IFolderOpener
{
    public bool IsAvailable => true;

    public async Task<bool> OpenAsync(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
#if WINDOWS
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
            return true;
#else
            // On Mac Catalyst a file URL for a folder opens it in Finder.
            return await MainThread.InvokeOnMainThreadAsync(() => Launcher.Default.OpenAsync(
                new UriBuilder { Scheme = Uri.UriSchemeFile, Host = "", Path = Path.TrimEndingDirectorySeparator(folder) + "/" }.Uri));
#endif
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or FeatureNotSupportedException)
        {
            return false;
        }
    }
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
