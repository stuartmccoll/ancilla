using Microsoft.JSInterop;

namespace Ancilla.UI.Services;

/// <summary>
/// Where the chosen theme is remembered between runs. The web host keeps it in a cookie that
/// theme.js writes, so the server can render the right theme on first paint; the desktop shell keeps
/// it in the app's own settings.
/// </summary>
public interface IThemePreference
{
    /// <summary>The saved theme ID, or null to use whatever the page shell already shows.</summary>
    string? Saved { get; }

    void Save(string id);
}

/// <summary>The web host's preference: theme.js sets the cookie, and the page shell reads it.</summary>
public sealed class CookieThemePreference : IThemePreference
{
    public string? Saved => null;

    public void Save(string id) { }
}

/// <summary>Saves text the app built (an export) as a file on this machine.</summary>
public interface IFileExport
{
    /// <returns>Where the file was saved, for display, or null when the browser asks the user where.</returns>
    /// <exception cref="JSException">The browser couldn't start the download.</exception>
    /// <exception cref="IOException">The file couldn't be written.</exception>
    Task<string?> SaveAsync(string fileName, string mime, string text);
}

/// <summary>The web host's export: theme.js turns the text into a download, so it never leaves the page.</summary>
public sealed class BrowserFileExport(IJSRuntime js) : IFileExport
{
    public async Task<string?> SaveAsync(string fileName, string mime, string text)
    {
        await js.InvokeVoidAsync("ancillaDownload", fileName, mime, text);
        return null;
    }
}
