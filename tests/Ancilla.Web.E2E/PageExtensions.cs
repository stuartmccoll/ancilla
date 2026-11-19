using Microsoft.Playwright;

namespace Ancilla.Web.E2E;

internal static class PageExtensions
{
    /// <summary>Opens a URL and waits for the interactive app to have rendered the page's heading.</summary>
    public static async Task OpenAsync(this IPage page, string path)
    {
        await page.GotoAsync(path);
        await page.Locator("main h1, main h3").First.WaitForAsync(new() { State = WaitForSelectorState.Attached });
    }

    /// <summary>Fails if Blazor's "An unhandled error has occurred" bar is showing.</summary>
    public static async Task AssertNoBlazorErrorAsync(this IPage page) =>
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
}
