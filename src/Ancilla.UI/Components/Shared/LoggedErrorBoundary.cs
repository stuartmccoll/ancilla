using Ancilla.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Ancilla.UI.Components.Shared;

/// <summary>
/// Catches an exception a page (or anything inside it) didn't, writes it to the
/// <see cref="DiagnosticLog"/> with its stack trace, and shows <see cref="ErrorBoundaryBase.ErrorContent"/>
/// in place of its content. The desktop app's web view only sends such an exception to the page's
/// console, never to .NET logging, so without this it isn't recorded anywhere. Call
/// <see cref="ErrorBoundaryBase.Recover"/> to show the content again.
/// </summary>
public sealed class LoggedErrorBoundary : ErrorBoundaryBase
{
    [Inject] private DiagnosticLog Log { get; set; } = default!;

    /// <summary>Where the boundary is, for the log line ("page", "app").</summary>
    [Parameter] public string Where { get; set; } = "page";

    protected override Task OnErrorAsync(Exception exception)
    {
        Log.Error($"Unhandled exception in the {Where}", exception);
        return Task.CompletedTask;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (CurrentException is null) builder.AddContent(0, ChildContent);
        else if (ErrorContent is not null) builder.AddContent(1, ErrorContent(CurrentException));
        else
        {
            builder.OpenElement(2, "p");
            builder.AddAttribute(3, "role", "alert");
            builder.AddContent(4, "Something went wrong. Reload the app to try again.");
            builder.CloseElement();
        }
    }
}
