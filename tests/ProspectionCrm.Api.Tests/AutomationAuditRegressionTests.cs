using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProspectionCrm.Api.Controllers;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationAuditRegressionTests
{
    private sealed class Capture<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull => null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        { Messages.Add(formatter(state,exception)); Exceptions.Add(exception); }
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ControlledErrorsDoNotLogExceptionPayloadsOrReturnSensitiveDetails(bool settings)
    {
        var settingsLog=new Capture<AutomationSettingsExceptionAttribute>();
        var supervisionLog=new Capture<AutomationSupervisionErrorsAttribute>();
        using var services=new ServiceCollection().AddSingleton<ILogger<AutomationSettingsExceptionAttribute>>(settingsLog)
            .AddSingleton<ILogger<AutomationSupervisionErrorsAttribute>>(supervisionLog).BuildServiceProvider();
        var http=new DefaultHttpContext { RequestServices=services };
        var context=new ExceptionContext(new ActionContext(http,new RouteData(),new ActionDescriptor()),[])
            { Exception=new InvalidOperationException("secret-marker SQL password") };
        if(settings) new AutomationSettingsExceptionAttribute().OnException(context);
        else new AutomationSupervisionErrorsAttribute("AutomationSupervisionInternalError").OnException(context);
        var messages=settings?settingsLog.Messages:supervisionLog.Messages;
        var exceptions=settings?settingsLog.Exceptions:supervisionLog.Exceptions;
        Assert.DoesNotContain("secret-marker",string.Join(" ",messages)); Assert.All(exceptions,Assert.Null);
        Assert.True(context.ExceptionHandled);
        var problem=Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(context.Result).Value);
        Assert.Equal(500,problem.Status); Assert.DoesNotContain("secret-marker",problem.Detail??"");
    }
}
