using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.AutomationActionRequests;
using ProspectionCrm.Api.Dtos.AutomationExecutions;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationActionRequestApiTests(AutomationJobDatabase database)
    : AutomationActionRequestFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task HttpDecisionsOnlyRecordIntentAndReadsRemainScoped()
    {
        var setup = await Prepare("assist"); await Run(setup); var request = await RequestFor(setup.Job.Id);
        var other = await Prepare("manual"); await Run(other); var foreign = await RequestFor(other.Job.Id);
        using var factory = Factory(setup.Workspace); using var client = Client(factory);
        var root = "/api/automation-action-requests";
        var page = await client.GetFromJsonAsync<AutomationActionRequestsPageDto>(root + "?status=pending&decisionRequirement=approval-required&limit=1");
        Assert.Equal(1, page!.TotalCount); Assert.Equal(request.Id, Assert.Single(page.Items).Id);
        Assert.False(page.Items[0].IsStale); Assert.NotNull(page.Items[0].ActionPlan);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + "/" + foreign.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(root + "/" + foreign.Id + "/approve", new { })).StatusCode);
        var approved = await client.PostAsJsonAsync(root + "/" + request.Id + "/approve", new { decisionNote = "Approved" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Empty(await Tasks(setup.Job.Id)); Assert.Empty(await Executions(setup.Job.Id));
        await Run(setup);
        var history = await client.GetFromJsonAsync<List<AutomationExecutionDto>>("/api/automation-executions?automationActionRequestId=" + request.Id);
        var execution = Assert.Single(history!); Assert.True(execution.IsHumanApprovedAttempt); Assert.False(execution.IsAutomaticAttempt);
        Assert.Equal(request.Id, execution.AutomationActionRequestId);
        Assert.Empty((await client.GetFromJsonAsync<List<AutomationExecutionDto>>("/api/automation-executions?automationActionRequestId=" + foreign.Id))!);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(root + "/" + request.Id + "/approve", new { decisionNote = "ignored" })).StatusCode);
        Assert.Equal("completed", (await Job(setup.Job.Id)).StatusCode);
    }

    [Theory]
    [InlineData("workspaceId")][InlineData("decidedByUserId")][InlineData("statusCode")]
    [InlineData("actionPlanJson")][InlineData("automationJobId")][InlineData("automationRuleId")][InlineData("decisionRequirementCode")]
    public async Task DecisionBodiesRejectClientAuthorityFields(string field)
    {
        var setup = await Prepare("manual"); await Run(setup); var request = await RequestFor(setup.Job.Id);
        using var factory = Factory(setup.Workspace); using var client = Client(factory);
        foreach (var action in new[] { "approve", "reject" })
        {
            var result = await client.PostAsync($"/api/automation-action-requests/{request.Id}/{action}",
                new StringContent("{\"" + field + "\":\"secret-marker\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            Assert.Contains("InvalidRequest", await result.Content.ReadAsStringAsync());
            Assert.DoesNotContain("secret-marker", await result.Content.ReadAsStringAsync());
        }
        Assert.Equal("pending", (await RequestFor(setup.Job.Id)).StatusCode);
    }

    [Theory]
    [InlineData("status=wrong")][InlineData("decisionRequirement=automatic")][InlineData("actionType=unknown")]
    [InlineData("offset=-1")][InlineData("limit=0")][InlineData("limit=201")]
    public async Task ListValidatesFilters(string query)
    {
        var setup = await Prepare(); using var factory = Factory(setup.Workspace); using var client = Client(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/automation-action-requests?" + query)).StatusCode);
    }

    [Fact]
    public async Task StaleReadAndDecisionNoteValidationDoNotMutateAnything()
    {
        var setup = await Prepare("manual"); await Run(setup); var request = await RequestFor(setup.Job.Id);
        using var factory = Factory(setup.Workspace); using var client = Client(factory); var url = "/api/automation-action-requests/" + request.Id;
        var tooLong = await client.PostAsJsonAsync(url + "/reject", new { decisionNote = new string('x', 2001) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode); Assert.Contains("InvalidDecisionNote", await tooLong.Content.ReadAsStringAsync());
        await ChangeRule(setup.Rule.Id, r => r.ActionConfigurationJson = """{"title":"Changed"}""");
        Assert.True((await client.GetFromJsonAsync<AutomationActionRequestDto>(url))!.IsStale);
        var stale = await client.PostAsJsonAsync(url + "/approve", new { }); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("ActionRequestStale", await stale.Content.ReadAsStringAsync());
        var reject = await client.PostAsJsonAsync(url + "/reject", new { decisionNote = "   " }); Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        Assert.Null((await RequestFor(setup.Job.Id)).DecisionNote); Assert.Empty(await Executions(setup.Job.Id));
    }

    [Fact]
    public async Task InvalidStoredPlanIsNeverReturnedRaw()
    {
        var setup = await Prepare("manual"); await Run(setup); var request = await RequestFor(setup.Job.Id);
        await using (var db = Db()) await db.AutomationActionRequests.Where(x => x.Id == request.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ActionPlanJson, "{\"secret-marker\":42}"));
        using var factory = Factory(setup.Workspace); using var client = Client(factory);
        var result = await client.GetAsync("/api/automation-action-requests/" + request.Id);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode); Assert.DoesNotContain("secret-marker", await result.Content.ReadAsStringAsync());
        Assert.Null((await result.Content.ReadFromJsonAsync<AutomationActionRequestDto>())!.ActionPlan);
    }
}
