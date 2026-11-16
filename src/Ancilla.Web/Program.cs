using Ancilla.Core;
using Ancilla.Theater;
using Ancilla.Web.Components;
using Ancilla.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Halo API calls run on the server, so the Spartan token never reaches the browser
// and there are no CORS issues with halowaypoint.com. Automatic redirects are off: a
// redirected request would carry the token's header to whatever host it was sent to.
builder.Services.AddHttpClient();
builder.Services.ConfigureHttpClientDefaults(http =>
    http.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false }));
builder.Services.AddSingleton(new SnapshotStore(builder.Configuration["Snapshots"] ?? SnapshotStore.DefaultPath));
// Theater films (9–30 MB each, every player's gamertag and XUID) are cached on disk outside the
// repo, in the same folder Ancilla.Theater.Cli uses, and downloaded only when a timeline is asked for.
builder.Services.AddSingleton(new FilmCache(builder.Configuration["Films"] ?? FilmCache.DefaultRoot));
builder.Services.AddSingleton<HaloCache>();
builder.Services.AddSingleton<AssetCache>();
builder.Services.AddScoped<HaloSession>();

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
    .AddInteractiveServerRenderMode();

app.Run();
