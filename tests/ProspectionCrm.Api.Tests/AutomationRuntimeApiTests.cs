using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.AutomationExecutions;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationRuntimeApiTests(AutomationJobDatabase database)
    : AutomationRuntimeFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task HistoryExposesJobAttemptAndReasonWithWorkspaceSafeFilters()
    {
        var a = await Prepare(); var b = await Prepare(); await Run(a); await Run(b);
        using var factory = Factory(a.Workspace); using var client = Client(factory);
        var histories = (await client.GetFromJsonAsync<List<AutomationExecutionDto>>(
            $"/api/automation-executions?automationJobId={a.Job.Id}"))!;
        var item = Assert.Single(histories);
        Assert.Equal(a.Job.Id, item.AutomationJobId); Assert.Equal(1, item.AttemptNumber);
        Assert.Equal("create-crm-task", item.ActionTypeCode); Assert.Equal("task-created", item.ReasonCode);
        Assert.True(item.EffectApplied);
        Assert.Empty((await client.GetFromJsonAsync<List<AutomationExecutionDto>>(
            $"/api/automation-executions?automationJobId={b.Job.Id}"))!);
        var foreign = Assert.Single(await Executions(b.Job.Id));
        using var inaccessible = await client.GetAsync($"/api/automation-executions/{foreign.Id}");
        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
    }

    [Fact]
    public async Task PreviewAndRepeatedDispatchRemainPureAndIdempotentAfterRuntimeIntegration()
    {
        var setup = await Prepare(); using var factory = Factory(setup.Workspace); using var client = Client(factory);
        var body = new { triggerTypeCode = "manual", eventKey = "phase84-api",
            payload = new { opportunityId = setup.Opportunity.Id } };
        using var one = await client.PostAsJsonAsync("/api/automation-events", body);
        using var two = await client.PostAsJsonAsync("/api/automation-events", body);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode); Assert.Equal(HttpStatusCode.OK, two.StatusCode);
        var first = (await one.Content.ReadFromJsonAsync<AutomationDispatchSummary>())!;
        var again = (await two.Content.ReadFromJsonAsync<AutomationDispatchSummary>())!;
        var jobId = Assert.Single(first.Jobs).JobId;
        Assert.Equal(jobId, Assert.Single(again.Jobs).JobId);
        var before = JsonSerializer.Serialize(await Job(jobId));
        for (var i = 0; i < 2; i++)
        {
            var preview = (await client.GetFromJsonAsync<AutomationEvaluationResult>(
                $"/api/automation-jobs/{jobId}/evaluation-preview"))!;
            Assert.Equal("automatic", preview.SafetyDecision!.DecisionCode);
        }
        Assert.Equal(before, JsonSerializer.Serialize(await Job(jobId)));
        Assert.Empty(await Executions(jobId)); Assert.Empty(await Tasks(jobId));
        await using var db = Db();
        Assert.Empty(await db.AutomationExecutions.Where(x => x.WorkspaceId == setup.Workspace).ToListAsync());
    }
}
