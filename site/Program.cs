namespace Ancilla.Site;

// dotnet run --project site -- --out _site [--release latest.json] [--screenshots folder]
//   --release      GitHub's "latest release" response, for the download links. Without it, the
//                  download page says the installers are coming soon.
//   --screenshots  PNGs named as in SiteBuilder.Screenshots (tests/Ancilla.Web.E2E writes them).

// A named class rather than top-level statements, so the browser tests can reference this project
// alongside the web host without two global Program classes.
public static class Cli
{
    public static async Task<int> Main(string[] args)
    {
        string? output = null, release = null, screenshots = null;
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--out": output = value; i++; break;
                case "--release": release = value; i++; break;
                case "--screenshots": screenshots = value; i++; break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 2;
            }
        }
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("Usage: --out <folder> [--release <latest.json>] [--screenshots <folder>]");
            return 2;
        }

        var latest = release is not null && File.Exists(release) ? Release.Parse(await File.ReadAllTextAsync(release)) : null;
        await SiteBuilder.BuildAsync(new SiteOptions(output, latest, screenshots));
        Console.WriteLine(latest is null
            ? "Built the site, with no release (the download page says coming soon)."
            : $"Built the site for {latest.Tag}.");
        return 0;
    }
}
