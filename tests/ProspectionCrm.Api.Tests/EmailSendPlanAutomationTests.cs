using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using ProspectionCrm.Api.Services.Email;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailSendPlanAutomationTests(AutomationJobDatabase database) : EmailSendPlanFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData("automatic", true)][InlineData("assist", true)][InlineData("manual", false)]
    public async Task WorkerPreparesTheSamePlanAndApprovalNeverResumesAnEmailExecutor(string mode, bool approve)
    {
        using var s = await Prepare(); await using var db = Db();
        var settings = await db.AutomationRuntimeSettings.SingleAsync(x => x.WorkspaceId == s.Workspace); settings.OperatingModeCode = mode;
        var rule = new AutomationRule { WorkspaceId = s.Workspace, Name = "Prepare email", TriggerTypeCode = "manual", Enabled = true,
            ActionTypeCode = EmailSendSnapshots.ActionType, ActionConfigurationJson = JsonSerializer.Serialize(Content(s), new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        db.Add(rule); await db.SaveChangesAsync();
        Assert.Null(AutomationRuleValidation.Validate(rule.TriggerTypeCode, rule.ActionTypeCode, rule.ConditionJson, rule.ActionConfigurationJson));
        await using var scope = s.Host.Services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IAutomationJobQueue>();
        var result = await queue.EnqueueAsync(s.Workspace, new() { AutomationRuleId = rule.Id, TriggerTypeCode = "manual", ActionCategoryCode = "email",
            ContextJson = """{"eventKey":"email-prepare","pipelineId":null,"payload":{}}""" }, default);
        Assert.Equal(201, result.Status);
        var job = (await queue.ClaimAsync(s.Workspace, default))!; Assert.NotNull(job);
        await scope.ServiceProvider.GetRequiredService<IAutomationJobProcessor>().ProcessAsync(job, job.LeaseOwner!, default);
        var request = await db.AutomationActionRequests.AsNoTracking().SingleAsync(x => x.AutomationJobId == job.Id);
        Assert.Equal(mode == "manual" ? "manual" : "approval-required", request.DecisionRequirementCode);
        var entity = await db.EmailSendPlans.AsNoTracking().SingleAsync(x => x.AutomationActionRequestId == request.Id);
        var p = await Read(s, entity.Id); Assert.Equal("pending-approval", p.StatusCode);
        Assert.Equal("awaiting-approval", (await db.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).StatusCode);
        Assert.Null(await queue.ClaimAsync(s.Workspace, default));
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, p, approve)).StatusCode);
        Assert.Equal(approve ? "completed" : "cancelled", (await db.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).StatusCode);
        Assert.Null(await queue.ClaimAsync(s.Workspace, default));
        Assert.Empty(await db.AutomationExecutions.Where(x => x.AutomationJobId == job.Id).ToListAsync());
        await AssertNoEffects(s);
    }
    [Theory]
    [InlineData("automatic", "assist", "approval-required")][InlineData("assist", "assist", "approval-required")][InlineData("manual", "manual", "manual")]
    public void EmailPolicyNeverAuthorizesAnAutomaticEffect(string requested, string effective, string decision)
    {
        var result = new AutomationSafetyPolicy().Evaluate(new() { IsEnabled = true, OperatingModeCode = requested }, "email");
        Assert.Equal(effective, result.EffectiveModeCode); Assert.Equal(decision, result.DecisionCode);
    }
}
