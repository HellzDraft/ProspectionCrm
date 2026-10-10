using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Npgsql;

namespace ProspectionCrm.Api.Controllers;

public sealed class AutomationSupervisionErrorsAttribute : ActionFilterAttribute, IExceptionFilter
{
    private readonly string internalCode;
    public AutomationSupervisionErrorsAttribute(string internalCode) { this.internalCode = internalCode; Order = -3000; }
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid) context.Result = Failure(400, "InvalidRequest");
    }
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested) return;
        var error = context.Exception.GetBaseException();
        var conflict = error is PostgresException { SqlState: "40001" or "40P01" or "23505" or "23503" };
        context.Result = Failure(conflict ? 409 : 500, conflict ? "ConcurrentCircuitReset" : internalCode);
        context.ExceptionHandled = true;
        context.HttpContext.RequestServices.GetRequiredService<ILogger<AutomationSupervisionErrorsAttribute>>()
            .LogError("Automation supervision request failed: {ExceptionType}", error.GetType().Name);
    }
    private static ObjectResult Failure(int status, string code)
    {
        var problem = new ProblemDetails { Status = status, Title = "Automation request failed" };
        problem.Extensions["code"] = code;
        return new(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
