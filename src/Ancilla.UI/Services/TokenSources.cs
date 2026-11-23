using Microsoft.Extensions.Configuration;

namespace Ancilla.UI.Services;

/// <summary>How the signed-in player's token was got.</summary>
public enum SignInMethod
{
    /// <summary>Pasted into the sign-in page.</summary>
    Pasted,
    /// <summary>The <c>HALO_SPARTAN_TOKEN</c> environment variable.</summary>
    Environment,
    /// <summary>Read from the desktop app's embedded halowaypoint.com sign-in (<see cref="IWaypointSignIn"/>).</summary>
    Waypoint,
}

/// <summary>
/// Where a Spartan token comes from, and whether a new one can be got when it runs out. A direct
/// Microsoft sign-in would be one more of these, if Microsoft ever approves an app registration.
/// A source hands its token over and keeps no copy: the token lives in the API client only.
/// </summary>
public interface ISpartanTokenSource
{
    SignInMethod Method { get; }

    /// <summary>A token, or null when there isn't one (nothing pasted or set, sign-in cancelled, cookie missing).</summary>
    Task<string?> GetTokenAsync(CancellationToken ct = default);

    /// <summary>A new token after the API refused the last one, or null when this source can't get one without you.</summary>
    Task<string?> RefreshAsync(CancellationToken ct = default);
}

/// <summary>A token pasted into the sign-in page. It can't be refreshed: you paste a new one.</summary>
public sealed class PastedToken(string token) : ISpartanTokenSource
{
    private string? _token = token;

    public SignInMethod Method => SignInMethod.Pasted;

    /// <summary>Hands the token over once and forgets it.</summary>
    public Task<string?> GetTokenAsync(CancellationToken ct = default) =>
        Task.FromResult(Interlocked.Exchange(ref _token, null) is { } t && !string.IsNullOrWhiteSpace(t) ? t : null);

    public Task<string?> RefreshAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}

/// <summary>
/// <c>HALO_SPARTAN_TOKEN</c>, read from configuration each time it's asked. A variable doesn't
/// change while the app runs, so there's nothing new to refresh to.
/// </summary>
public sealed class EnvironmentToken(IConfiguration config) : ISpartanTokenSource
{
    public const string Variable = "HALO_SPARTAN_TOKEN";

    public SignInMethod Method => SignInMethod.Environment;

    public Task<string?> GetTokenAsync(CancellationToken ct = default) =>
        Task.FromResult(config[Variable] is { } t && !string.IsNullOrWhiteSpace(t) ? t : null);

    public Task<string?> RefreshAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}

/// <summary>
/// The desktop app's embedded sign-in. The first token comes from halowaypoint.com's sign-in page
/// in a window (<paramref name="interactive"/>) or, at start-up with "Stay signed in", from a hidden
/// one. Later tokens always come from a hidden sign-in, which works while Microsoft's session lasts.
/// </summary>
public sealed class WaypointToken(IWaypointSignIn signIn, bool interactive) : ISpartanTokenSource
{
    public SignInMethod Method => SignInMethod.Waypoint;

    public Task<string?> GetTokenAsync(CancellationToken ct = default) =>
        interactive ? signIn.SignInAsync(ct) : signIn.SilentSignInAsync(ct);

    public Task<string?> RefreshAsync(CancellationToken ct = default) => signIn.SilentSignInAsync(ct);
}
