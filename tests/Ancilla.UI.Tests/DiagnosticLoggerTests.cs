using Ancilla.Core;
using Ancilla.Testing;
using Ancilla.UI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ancilla.UI.Tests;

/// <summary>Errors logged through <see cref="ILogger"/>, as Blazor logs a page's unhandled exception, reach the log files.</summary>
public sealed class DiagnosticLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ancilla-logger-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Logged() => Directory.Exists(_dir)
        ? string.Concat(Directory.EnumerateFiles(_dir).Select(File.ReadAllText))
        : "";

    [Fact]
    public void Errors_go_to_the_log_with_their_stack_trace_and_nothing_quieter_does()
    {
        using var provider = new DiagnosticLoggerProvider(new DiagnosticLog(_dir, new Redactor()));
        var logger = provider.CreateLogger("Microsoft.AspNetCore.Components.WebView.Services.WebViewRenderer");
        Exception thrown;
        try { throw new NullReferenceException($"no skill for xuid({Spartans.NobleSix.Xuid})"); }
        catch (Exception ex) { thrown = ex; }

        logger.LogInformation("Navigating");
        logger.LogWarning("Slow");
        logger.LogError(thrown, "Unhandled exception rendering component: {Message}", thrown.Message);

        var text = Logged();
        Assert.Contains("Error in Microsoft.AspNetCore.Components.WebView.Services.WebViewRenderer: Unhandled exception rendering component", text);
        Assert.Contains("    System.NullReferenceException: no skill for xuid([xuid])", text);
        Assert.Contains(nameof(Errors_go_to_the_log_with_their_stack_trace_and_nothing_quieter_does), text);
        Assert.DoesNotContain("Navigating", text);
        Assert.DoesNotContain("Slow", text);
        Assert.DoesNotContain(Spartans.NobleSix.Xuid, text);
    }

    [Fact]
    public void Both_hosts_logger_factories_pick_it_up_from_AddAncilla()
    {
        // Every folder AddAncilla would use, in the test's own, so nothing touches the real ones.
        var config = new ConfigurationBuilder().AddInMemoryCollection([
            new("Logs", _dir), new("Archive", Path.Combine(_dir, "a.db")), new("Snapshots", Path.Combine(_dir, "s.json")),
            new("Films", Path.Combine(_dir, "films")), new("Settings", Path.Combine(_dir, "settings.json"))]).Build();
        using var sp = new ServiceCollection().AddLogging().AddSingleton<IWaypointSignIn, NoWaypointSignIn>()
            .AddAncilla(config).BuildServiceProvider();

        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Test").LogCritical("It broke");

        Assert.Contains("Critical in Test: It broke", Logged());
    }
}
