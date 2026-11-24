using Microsoft.JSInterop;

namespace Ancilla.UI.Services;

/// <summary>
/// The theme this window shows, shared by every picker in it (the user menu and Settings), so
/// picking in one shows in the other. Scoped: one per circuit on the web host, one per window in
/// the desktop app. theme.js applies it to the page; <see cref="IThemePreference"/> remembers it.
/// </summary>
public sealed class ThemeChoice(IJSRuntime js, IThemePreference preference)
{
    private bool _loaded;

    public string Current { get; private set; } = Themes.Default.Id;

    /// <summary>Raised when <see cref="Current"/> changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Reads the theme once, after the first render (JS isn't there before). The desktop app applies
    /// its saved theme; the web host's page shell already shows the cookie's.
    /// </summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        try
        {
            if (preference.Saved is { } saved)
            {
                Current = Themes.Normalize(saved);
                await js.InvokeVoidAsync("haloTheme.set", Current);
            }
            else Current = Themes.Normalize(await js.InvokeAsync<string>("haloTheme.get"));
            _loaded = true;
            Changed?.Invoke();
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }   // no JS (e.g. static rendering)
    }

    public async Task SetAsync(string id)
    {
        Current = Themes.Normalize(id);
        _loaded = true;
        preference.Save(Current);
        Changed?.Invoke();
        try { await js.InvokeVoidAsync("haloTheme.set", Current); }
        catch (JSException) { }
    }

    /// <summary>Forgets the saved theme (the desktop app's setting, or the web host's cookie) and shows the default.</summary>
    public async Task ForgetAsync()
    {
        Current = Themes.Default.Id;
        preference.Forget();
        Changed?.Invoke();
        try { await js.InvokeVoidAsync("haloTheme.forget"); }
        catch (JSException) { }
    }
}
