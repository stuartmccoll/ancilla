using Ancilla.Testing;
using Ancilla.UI.Services;

namespace Ancilla.UI.Tests;

/// <summary>
/// The desktop app's embedded sign-in, without a browser. <see cref="Cookie"/> stands for the
/// <c>343-spartan-token</c> cookie the sign-in would find (null: missing), and
/// <see cref="HasMicrosoftSession"/> for whether a hidden sign-in would work.
/// </summary>
public sealed class FakeWaypointSignIn : IWaypointSignIn
{
    public bool IsAvailable => true;
    public bool StaySignedIn { get; set; }

    /// <summary>What the next sign-in's cookie holds. Null: the cookie isn't there.</summary>
    public string? Cookie { get; set; } = Spartans.Token;

    /// <summary>Whether Microsoft still has a session, so a hidden sign-in gets a token.</summary>
    public bool HasMicrosoftSession { get; set; }

    /// <summary>Makes the sign-in window act as if it was closed before signing in.</summary>
    public bool Cancelled { get; set; }

    /// <summary>Makes a hidden sign-in throw, as a web view might.</summary>
    public Exception? SilentFailure { get; set; }

    public int SignIns { get; private set; }
    public int SilentSignIns { get; private set; }
    public int SignOuts { get; private set; }

    public Task<string?> SignInAsync(CancellationToken ct = default)
    {
        SignIns++;
        if (Cancelled) return Task.FromResult<string?>(null);
        HasMicrosoftSession = true;
        return Task.FromResult(Cookie);
    }

    public async Task<string?> SilentSignInAsync(CancellationToken ct = default)
    {
        SilentSignIns++;
        await Task.Yield();
        if (SilentFailure is { } ex) throw ex;
        return HasMicrosoftSession ? Cookie : null;
    }

    public Task SignOutAsync()
    {
        SignOuts++;
        HasMicrosoftSession = false;
        return Task.CompletedTask;
    }
}
