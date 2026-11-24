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

    /// <summary>Removes the saved theme, so the default shows next time.</summary>
    void Forget();
}

/// <summary>The web host's preference: theme.js sets the cookie, and the page shell reads it.</summary>
public sealed class CookieThemePreference : IThemePreference
{
    public string? Saved => null;

    public void Save(string id) { }

    public void Forget() { }
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

/// <summary>Puts text on the system clipboard.</summary>
public interface IClipboardWriter
{
    /// <exception cref="JSException">The browser wouldn't allow it.</exception>
    Task SetTextAsync(string text);
}

/// <summary>The web host's: the browser's clipboard, which allows it on localhost straight after a click.</summary>
public sealed class BrowserClipboard(IJSRuntime js) : IClipboardWriter
{
    public async Task SetTextAsync(string text) => await js.InvokeVoidAsync("navigator.clipboard.writeText", text);
}

/// <summary>Shows a folder in the system's file manager (Finder, File Explorer).</summary>
public interface IFolderOpener
{
    /// <summary>False on the web host: a browser page can't open folders on the machine.</summary>
    bool IsAvailable { get; }

    /// <returns>Whether the file manager opened it.</returns>
    Task<bool> OpenAsync(string folder);
}

/// <summary>The web host's: not available, so pages show the folder's path instead.</summary>
public sealed class NoFolderOpener : IFolderOpener
{
    public bool IsAvailable => false;

    public Task<bool> OpenAsync(string folder) => Task.FromResult(false);
}

/// <summary>
/// Signing in on halowaypoint.com itself, in a browser embedded in the app, so Ancilla never sees
/// the password. Only the desktop app can embed one; the web host's says it isn't available.
/// Each method returns the <c>343-spartan-token</c> cookie's value, or null.
/// </summary>
public interface IWaypointSignIn
{
    bool IsAvailable { get; }

    /// <summary>
    /// Keep Microsoft's sign-in on this computer between runs. Off, the embedded browser keeps
    /// nothing on disk. Turning it off removes a kept sign-in.
    /// </summary>
    bool StaySignedIn { get; set; }

    /// <summary>Shows halowaypoint.com's sign-in page. Null if it was closed before signing in.</summary>
    Task<string?> SignInAsync(CancellationToken ct = default);

    /// <summary>
    /// Signs in again in a browser nobody sees, using Microsoft's session from an earlier sign-in.
    /// Null when there's no session (signed out, it ran out, or Microsoft wants you to sign in).
    /// </summary>
    Task<string?> SilentSignInAsync(CancellationToken ct = default);

    /// <summary>Removes everything the embedded browser kept, Microsoft's sign-in included.</summary>
    Task SignOutAsync();
}

/// <summary>The web host's: a browser tab can't read halowaypoint.com's cookies, so you paste the token.</summary>
public sealed class NoWaypointSignIn : IWaypointSignIn
{
    public bool IsAvailable => false;

    public bool StaySignedIn { get => false; set { } }

    public Task<string?> SignInAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task<string?> SilentSignInAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task SignOutAsync() => Task.CompletedTask;
}
