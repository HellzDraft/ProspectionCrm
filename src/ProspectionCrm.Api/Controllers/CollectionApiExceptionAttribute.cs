using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ProspectionCrm.Api.Controllers;

// Only Phase 7 API boundaries: keep unexpected storage diagnostics in logs,
// including when the Development exception page is enabled.
public sealed class CollectionApiExceptionAttribute(string code) : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested) return;
        context.HttpContext.RequestServices.GetRequiredService<ILogger<CollectionApiExceptionAttribute>>()
            .LogError(context.Exception, "Collection API request failed: {Code}", code);
        var problem = new ProblemDetails
        {
            Status = 500, Title = "Collection request failed",
            Detail = "The collection request could not be completed."
        };
        problem.Extensions["code"] = code;
        context.Result = new ObjectResult(problem) { StatusCode = 500, ContentTypes = { "application/problem+json" } };
        context.ExceptionHandled = true;
    }
}
