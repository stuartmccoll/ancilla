using Ancilla.Site.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ancilla.Site;

/// <summary>A page of the site: its file, its name in the navigation, and the component that renders it.</summary>
public sealed record SitePage(string File, string Title, Type Component);

/// <summary>
/// A screenshot of the app for the home page. The site workflow takes them from the web dev host
/// running against the fake backend (tests/Ancilla.Web.E2E, <c>SiteScreenshots</c>), so they only
/// ever show made-up players.
/// </summary>
public sealed record Screenshot(string File, string Caption, string Alt);

/// <summary>What the pages are built from.</summary>
public sealed record SiteModel(Release? Release, IReadOnlyList<Screenshot> Screenshots)
{
    public const string Repo = "https://github.com/stuartmccoll/ancilla";
    public const string Releases = Repo + "/releases";
    public const string Readme = Repo + "#readme";
    public const string Issues = Repo + "/issues";
}

public sealed record SiteOptions(string OutputFolder, Release? Release = null, string? ScreenshotsFolder = null);

public static class SiteBuilder
{
    public static readonly IReadOnlyList<SitePage> Pages =
    [
        new("index.html", "Home", typeof(Home)),
        new("download.html", "Download", typeof(Download)),
        new("getting-started.html", "Getting started", typeof(GettingStarted)),
        new("privacy.html", "Privacy", typeof(Privacy)),
        new("disclaimer.html", "Disclaimer", typeof(Disclaimer)),
    ];

    /// <summary>Every screenshot the home page can show, in order. Missing files are left out.</summary>
    public static readonly IReadOnlyList<Screenshot> Screenshots =
    [
        new("progress.png", "How many matches until you reach Hero and finish your battle passes.",
            "The Progress page: career XP per match, about 34 matches to Hero, and progress towards the next career rank."),
        new("stats.png", "Your service record, with PvP figures worked out for you.",
            "The service record: tiles for matches, win rate, time played, K/D, KDA and accuracy, then ranked CSR and most earned medals."),
        new("match.png", "Every match in detail, with the matchmaker's expected kills and deaths.",
            "A match's page: a Slayer loss on Recharge, the player's kills, deaths, accuracy and damage, and kills and deaths against what the matchmaker expected."),
        new("timeline.png", "A timeline of each match, read from its Theater film.",
            "A match timeline: a bar chart of each team's kills by minute, above a table of each player's kills, deaths and best streak."),
        new("trends.png", "Trends across your recent matches.",
            "The Trends page: tiles for win rate, K/D and accuracy, and a chart of K/D in each match with a 10-game average."),
    ];

    /// <summary>Writes the whole site to <see cref="SiteOptions.OutputFolder"/>, replacing what was there.</summary>
    public static async Task BuildAsync(SiteOptions options)
    {
        var output = Path.GetFullPath(options.OutputFolder);
        if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(output);

        var shots = new List<Screenshot>();
        if (options.ScreenshotsFolder is { } folder)
        {
            Directory.CreateDirectory(Path.Combine(output, "screenshots"));
            foreach (var shot in Screenshots)
            {
                var source = Path.Combine(folder, shot.File);
                if (!File.Exists(source)) continue;
                File.Copy(source, Path.Combine(output, "screenshots", shot.File));
                shots.Add(shot);
            }
        }
        var model = new SiteModel(options.Release, shots);

        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance);
        foreach (var page in Pages)
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { ["Site"] = model });
                var root = await renderer.RenderComponentAsync(page.Component, parameters);
                return root.ToHtmlString();
            });
            await File.WriteAllTextAsync(Path.Combine(output, page.File), "<!DOCTYPE html>\n" + html);
        }

        var assembly = typeof(SiteBuilder).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var target = Path.Combine(output, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = assembly.GetManifestResourceStream(name)!;
            await using var file = File.Create(target);
            await source.CopyToAsync(file);
        }
        // GitHub Pages runs Jekyll over the site unless told not to.
        await File.WriteAllTextAsync(Path.Combine(output, ".nojekyll"), "");
    }
}
