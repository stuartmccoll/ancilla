using Ancilla.Core;
using Ancilla.UI.Services;
using Ancilla.Web.Components;

// The web dev host for Ancilla.UI. It's for development (hot reload) and the browser tests; the app
// people run is the desktop shell, src/Ancilla.App. It listens on localhost only (see launchSettings.json).
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAncilla(builder.Configuration);
// One session per circuit, so each browser tab signs in on its own.
builder.Services.AddScoped<HaloSession>();
builder.Services.AddSingleton<IThemePreference, CookieThemePreference>();
builder.Services.AddScoped<IFileExport, BrowserFileExport>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

// Halo CMS images that need the token to download are fetched by a signed-in session and
// served from memory here, so the browser never needs (or sees) the token.
app.MapGet("/assets/{**key}", (string key, AssetCache assets, HttpContext ctx) =>
{
    if (!assets.TryGet(key, out var a)) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "public, max-age=604800, immutable";
    return Results.File(a.Bytes, a.ContentType);
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Ancilla.UI.Components.Routes).Assembly);

app.Services.GetRequiredService<HaloDiagnostics>().Info($"Ancilla {DiagnosticsReport.AppVersion} started");
app.Run();

// Lets the browser tests (tests/Ancilla.Web.E2E) host the app with WebApplicationFactory.
public partial class Program;
