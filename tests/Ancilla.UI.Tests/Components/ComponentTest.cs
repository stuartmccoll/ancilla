using Ancilla.UI.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Ancilla.UI.Tests.Components;

/// <summary>
/// A bUnit context with the app's services, wired to the fake Halo backend. Call
/// <see cref="SignInAsync"/> or <see cref="SignedOut"/> before rendering: services can't be
/// added once the first component has rendered.
/// </summary>
public abstract class ComponentTest : BunitContext
{
    protected ComponentTest(Harness? harness = null)
    {
        H = harness ?? new Harness();
        // theme.js and the timeline's module aren't loaded here; calls to them return defaults.
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(H.Snapshots);
        Services.AddSingleton(H.Films);
        Services.AddSingleton(H.Archive);
        Services.AddSingleton(H.Cache);
        Services.AddSingleton(H.Assets);
        Services.AddSingleton(H.Log);
        Services.AddSingleton(H.Diagnostics);
        Services.AddSingleton(H.Report);
        // The web host's: theme.js keeps the theme, and exports are browser downloads.
        Services.AddSingleton<IThemePreference>(Theme);
        Services.AddScoped<IFileExport, BrowserFileExport>();
        Services.AddScoped<IClipboardWriter, BrowserClipboard>();
        Services.AddSingleton<IFolderOpener, NoFolderOpener>();
        Services.AddSingleton<IWaypointSignIn>(_ => H.Waypoint);
        Services.AddSingleton(H.Settings);
        Services.AddSingleton(_ => H.Data);
        Services.AddScoped<ThemeChoice>();
    }

    /// <summary>The theme preference pages see. Set <see cref="SavedTheme.Saved"/> to act as the desktop shell.</summary>
    protected SavedTheme Theme { get; } = new();

    protected sealed class SavedTheme : IThemePreference
    {
        public string? Saved { get; set; }
        public void Save(string id) => Saved = id;
        public void Forget() => Saved = null;
    }

    protected Harness H { get; }
    protected HaloSession Session { get; private set; } = null!;

    protected async Task SignInAsync()
    {
        Session = await H.SignedInAsync();
        Services.AddSingleton(Session);
    }

    protected void SignedOut()
    {
        Session = H.NewSession();
        Services.AddSingleton(Session);
    }

    /// <summary>How many requests the fake has had whose URL contains <paramref name="fragment"/>.</summary>
    protected int Requests(string fragment) =>
        H.Halo.Requests.Count(r => r.Uri.ToString().Contains(fragment, StringComparison.OrdinalIgnoreCase));

    protected static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Clicks the element matching <paramref name="selector"/> whose text is <paramref name="text"/>.
    /// Pages re-render as background data arrives, which can retire an element's event handler
    /// between finding it and clicking it, so both happen together on the renderer's thread.
    /// </summary>
    protected static void Click<T>(IRenderedComponent<T> cut, string selector, string text) where T : Microsoft.AspNetCore.Components.IComponent
    {
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(selector), e => e.TextContent.Trim() == text), Wait);
        OnRenderer(cut, () => cut.FindAll(selector).Single(e => e.TextContent.Trim() == text).Click());
    }

    /// <summary>Runs a find-then-act step on the renderer's thread, so no render lands in between.</summary>
    protected static void OnRenderer<T>(IRenderedComponent<T> cut, Action act) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.InvokeAsync(act).GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) H.Dispose();
    }
}
