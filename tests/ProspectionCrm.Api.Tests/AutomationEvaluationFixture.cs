using System.Net;
using System.Text.Json.Nodes;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public abstract class AutomationEvaluationFixture(AutomationJobDatabase database) : AutomationJobFixture(database)
{
    protected static AutomationEventDispatcher Dispatcher(ProspectionCrmDbContext db) => new(db, Queue(db));
    protected async Task<Guid> Pipeline(Guid workspace)
    {
        await using var db = Db(); var pipeline = new Pipeline { WorkspaceId = workspace, Name = "Pipeline", TypeCode = "business" };
        db.Add(pipeline); await db.SaveChangesAsync(); return pipeline.Id;
    }
    protected async Task<AutomationRule> ValidRule(Guid workspace, Guid? pipeline = null, Action<AutomationRule>? change = null)
    {
        var rule = new AutomationRule { WorkspaceId = workspace, PipelineId = pipeline, Name = "Rule", TriggerTypeCode = "manual",
            ActionTypeCode = "create-crm-task", ActionConfigurationJson = """{"title":"Relancer","dueInDays":3}""" };
        change?.Invoke(rule); await using var db = Db(); db.Add(rule); await db.SaveChangesAsync(); return rule;
    }
    protected const string Payload = """{"opportunityId":"00000000-0000-0000-0000-000000000001","source":"manual"}""";
    protected static Task Problem(HttpResponseMessage response, HttpStatusCode status, string code) => AssertProblem(response, status, code);
    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(code, json["code"]!.GetValue<string>());
        Assert.DoesNotContain("secret-marker", json.ToJsonString());
    }
}
