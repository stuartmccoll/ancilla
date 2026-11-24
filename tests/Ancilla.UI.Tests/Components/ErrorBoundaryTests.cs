using Ancilla.Testing;
using Ancilla.UI.Components.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Ancilla.UI.Tests.Components;

/// <summary>
/// A page's unhandled exception is written to the log, with its stack trace, and the layout shows a
/// message in its place, instead of the app's "unhandled error" bar with nothing recorded.
/// </summary>
public sealed class ErrorBoundaryTests : ComponentTest
{
    private string Logged() => Directory.Exists(H.Log.Folder)
        ? string.Concat(Directory.EnumerateFiles(H.Log.Folder).Select(File.ReadAllText))
        : "";

    private IRenderedComponent<MainLayout> RenderLayoutWith<T>() where T : IComponent =>
        Render<MainLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => { b.OpenComponent<T>(0); b.CloseComponent(); })));

    [Fact]
    public async Task A_page_that_throws_while_loading_is_logged_and_replaced_by_a_message()
    {
        await SignInAsync();
        var cut = RenderLayoutWith<ThrowsOnLoad>();

        cut.WaitForAssertion(() => Assert.Equal("Something went wrong", cut.Find(".page-error h1").TextContent), Wait);
        Assert.NotEmpty(cut.FindAll("nav a"));   // the rest of the layout is still there
        var text = Logged();
        Assert.Contains("Unhandled exception in the page", text);
        Assert.Contains($"    System.InvalidOperationException: No scoreboard for xuid([xuid])", text);
        Assert.Contains(nameof(ThrowsOnLoad), text);   // the stack trace
        Assert.DoesNotContain(Spartans.NobleSix.Xuid, text);
    }

    [Fact]
    public async Task A_page_that_throws_from_a_click_is_logged_and_can_be_tried_again()
    {
        await SignInAsync();
        var cut = RenderLayoutWith<ThrowsOnClick>();

        Click(cut, "button", "Load older");
        cut.WaitForAssertion(() => Assert.Contains("Load older matches failed", Logged()), Wait);
        Click(cut, "button", "Try again");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button"), b => b.TextContent == "Load older"), Wait);
        Assert.Empty(cut.FindAll(".page-error"));
    }

    private sealed class ThrowsOnLoad : ComponentBase
    {
        protected override async Task OnInitializedAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException($"No scoreboard for xuid({Spartans.NobleSix.Xuid})");
        }
    }

    private sealed class ThrowsOnClick : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenElement(0, "button");
            b.AddAttribute(1, "onclick", EventCallback.Factory.Create(this, () => throw new InvalidOperationException("Load older matches failed")));
            b.AddContent(2, "Load older");
            b.CloseElement();
        }
    }
}
