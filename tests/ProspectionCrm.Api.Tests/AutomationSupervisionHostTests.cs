using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationSupervisionHostTests(AutomationJobDatabase database)
    : AutomationSupervisionFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData("PendingJobWarningAgeMinutes","0")][InlineData("ApprovalWarningAgeHours","0")]
    [InlineData("BacklogWarningCount","0")][InlineData("RecentFailureWindowHours","0")]
    public void InvalidSupervisionOptionsFailOnStartupEvenWithWorkerDisabled(string option,string value)
    {
        using var factory=SupervisionFactory(null).WithWebHostBuilder(b=>b.UseSetting("AutomationSupervision:"+option,value));
        Assert.Throws<OptionsValidationException>(()=>Client(factory));
    }
}
