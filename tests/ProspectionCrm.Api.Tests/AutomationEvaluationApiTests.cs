using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.AutomationRules;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationEvaluationApiTests(AutomationJobDatabase database) : AutomationEvaluationFixture(database), IClassFixture<AutomationJobDatabase>
{
    private const string Events = "/api/automation-events";
    private static string Preview(Guid id) => $"/api/automation-jobs/{id}/evaluation-preview";
    private static object Event(string key = "event", Guid? pipeline = null) => new
        { triggerTypeCode = "manual", eventKey = key, pipelineId = pipeline, payload = JsonSerializer.Deserialize<JsonElement>(Payload) };

    [Fact]
    public async Task UnavailableWorkspaceHasControlledResponsesWithoutImplicitBootstrap()
    {
        await Workspace(); await Workspace();
        using var factory = Factory(null); using var client = Client(factory);
        using var dispatch = await client.PostAsJsonAsync(Events, Event());
        await Problem(dispatch, HttpStatusCode.Conflict, "WorkspaceUnavailable");
        using var preview = await client.GetAsync(Preview(Guid.NewGuid()));
        await Problem(preview, HttpStatusCode.Conflict, "WorkspaceUnavailable");
    }

    [Fact]
    public async Task OmittedPayloadIsAnEmptyObjectAndInvalidTargetIsDeferredToEvaluation()
    {
        var workspace = await Workspace(); await ValidRule(workspace);
        using var factory = Factory(workspace); using var client = Client(factory);
        using var response = await client.PostAsJsonAsync(Events, new { triggerTypeCode = "manual", eventKey = "empty" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = (await response.Content.ReadFromJsonAsync<AutomationDispatchSummary>())!;
        var result = (await client.GetFromJsonAsync<AutomationEvaluationResult>(Preview(summary.Jobs.Single().JobId)))!;
        Assert.False(result.IsValid); Assert.Equal("invalid-opportunity-id", result.ReasonCode); Assert.Null(result.ActionPlan);
    }

    [Fact]
    public async Task DispatchUsesOnlyProviderWorkspaceAndReturnsZeroAndIdempotentSummaries()
    {
        var workspace = await Workspace(); var foreign = await Workspace(); await ValidRule(foreign);
        using var factory = Factory(workspace); using var client = Client(factory);
        using var empty = await client.PostAsJsonAsync(Events + "?workspaceId=" + foreign, Event());
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        var zero = (await empty.Content.ReadFromJsonAsync<AutomationDispatchSummary>())!;
        Assert.Equal(0, zero.CandidateRuleCount); Assert.Equal(0, zero.CreatedCount); Assert.Empty(zero.Jobs);
        var rule = await ValidRule(workspace);
        using var first = await client.PostAsJsonAsync(Events, Event()); using var second = await client.PostAsJsonAsync(Events, Event());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var created = (await first.Content.ReadFromJsonAsync<AutomationDispatchSummary>())!;
        var existing = (await second.Content.ReadFromJsonAsync<AutomationDispatchSummary>())!;
        Assert.Equal("manual", created.TriggerTypeCode); Assert.Equal("event", created.EventKey);
        Assert.Equal(1, created.CreatedCount); Assert.Equal(1, existing.ExistingCount); Assert.Equal(0, existing.CreatedCount);
        Assert.Equal(rule.Id, Assert.Single(created.Jobs).AutomationRuleId); Assert.Equal(created.Jobs[0].JobId, existing.Jobs[0].JobId);
        await using var db = Db(); Assert.Equal(workspace, (await db.AutomationJobs.SingleAsync(x => x.WorkspaceId == workspace)).WorkspaceId);
    }

    [Theory]
    [InlineData("{}", "InvalidRequest")]
    [InlineData("null", "InvalidRequest")]
    [InlineData("{secret-marker}", "InvalidRequest")]
    [InlineData("""{"triggerTypeCode":"unknown","eventKey":"event"}""", "unsupported-trigger")]
    [InlineData("""{"triggerTypeCode":"manual","eventKey":" "}""", "InvalidRequest")]
    [InlineData("""{"triggerTypeCode":"manual","eventKey":"a\nb"}""", "invalid-event-key")]
    [InlineData("""{"triggerTypeCode":"manual","eventKey":"event","payload":[]}""", "invalid-payload")]
    [InlineData("""{"triggerTypeCode":"manual","eventKey":"event","payload":null}""", "invalid-payload")]
    [InlineData("""{"triggerTypeCode":"manual","eventKey":"event","payload":{"x":1,"x":2}}""", "invalid-payload")]
    [InlineData("""{"triggerTypeCode":"manual","eventKey":"event","payload":{"x":"\u0000"}}""", "invalid-payload")]
    [InlineData("""{"triggerTypeCode":"manual","eventKey":"event","extra":"secret-marker"}""", "InvalidRequest")]
    [InlineData("""{"triggerTypeCode":"manual","eventKey":"event","workspaceId":"00000000-0000-0000-0000-000000000001"}""", "InvalidRequest")]
    [InlineData("""{"triggerTypeCode":"manual","eventKey":"event","pipelineId":"secret-marker"}""", "InvalidRequest")]
    [InlineData("""{"triggerTypeCode":"manual","eventKey":"event","pipelineId":"00000000-0000-0000-0000-000000000000"}""", "invalid-pipeline")]
    public async Task InvalidDispatchRequestsReturnControlledErrors(string body, string code)
    {
        var workspace = await Workspace(); await ValidRule(workspace);
        using var factory = Factory(workspace); using var client = Client(factory);
        using var response = await client.PostAsync(Events, new StringContent(body, Encoding.UTF8, "application/json"));
        await Problem(response, HttpStatusCode.BadRequest, code);
        await using var db = Db(); Assert.Empty(await db.AutomationJobs.Where(x => x.WorkspaceId == workspace).ToListAsync());
    }

    [Fact]
    public async Task ForeignPipelineAndSizeLimitsFailWithoutDisclosureOrWrites()
    {
        var workspace = await Workspace(); var foreignPipeline = await Pipeline(await Workspace()); await ValidRule(workspace);
        using var factory = Factory(workspace); using var client = Client(factory);
        foreach (var id in new[] { foreignPipeline, Guid.NewGuid() })
        {
            using var response = await client.PostAsJsonAsync(Events, Event(pipeline: id));
            await Problem(response, HttpStatusCode.NotFound, "pipeline-not-found");
            Assert.DoesNotContain(id.ToString(), await response.Content.ReadAsStringAsync());
        }
        using var key = await client.PostAsJsonAsync(Events, Event(new string('x', 101)));
        await Problem(key, HttpStatusCode.BadRequest, "InvalidRequest");
        using var large = await client.PostAsJsonAsync(Events, new { triggerTypeCode = "manual", eventKey = "event", payload = new { value = new string('x', 17000) } });
        await Problem(large, HttpStatusCode.BadRequest, "invalid-payload");
        await using var db = Db(); Assert.Empty(await db.AutomationJobs.Where(x => x.WorkspaceId == workspace).ToListAsync());
    }

    [Fact]
    public async Task PreviewIsRepeatableReadOnlyAndUsesCurrentRuleAndSettingsEvenForLeasedJobs()
    {
        var workspace = await Workspace(); var rule = await ValidRule(workspace);
        await using var db = Db();
        var dispatch = (await Dispatcher(db).DispatchAsync(new(workspace, "manual", "event", PayloadJson: Payload), default)).Value!;
        var id = dispatch.Jobs.Single().JobId; await Queue(db).ClaimAsync(workspace, default);
        async Task<string> JobSnapshot() => JsonSerializer.Serialize(await db.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == id));
        var before = await JobSnapshot();
        using var factory = Factory(workspace); using var client = Client(factory);
        var blocked = (await client.GetFromJsonAsync<AutomationEvaluationResult>(Preview(id)))!;
        Assert.Equal("blocked", blocked.SafetyDecision!.DecisionCode); Assert.Null(blocked.ActionPlan);
        Assert.Equal(blocked, await client.GetFromJsonAsync<AutomationEvaluationResult>(Preview(id)));
        await db.AutomationRuntimeSettings.Where(x => x.WorkspaceId == workspace).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.IsEnabled, true).SetProperty(x => x.OperatingModeCode, "automatic"));
        await db.AutomationRules.Where(x => x.Id == rule.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.ActionConfigurationJson, "{\"title\":\"Changed\"}"));
        var automatic = (await client.GetFromJsonAsync<AutomationEvaluationResult>(Preview(id)))!;
        Assert.Equal("automatic", automatic.SafetyDecision!.DecisionCode); Assert.Equal("Changed", automatic.ActionPlan!.Title);
        Assert.Null(automatic.ActionPlan.DueInDays);
        Assert.Equal(automatic, await client.GetFromJsonAsync<AutomationEvaluationResult>(Preview(id)));
        await db.AutomationRules.Where(x => x.Id == rule.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        Assert.Equal("rule-disabled", (await client.GetFromJsonAsync<AutomationEvaluationResult>(Preview(id)))!.ReasonCode);
        Assert.Equal(before, await JobSnapshot());
        Assert.Empty(await db.AutomationExecutions.ToListAsync()); Assert.Empty(await db.CrmTasks.ToListAsync());
        Assert.Empty(await db.Opportunities.ToListAsync()); Assert.Empty(await db.ActivityEntries.ToListAsync());
        Assert.Empty(await db.EmailMessages.ToListAsync()); Assert.Empty(await db.Applications.ToListAsync()); Assert.Empty(await db.Proposals.ToListAsync());
    }

    [Fact]
    public async Task PreviewReturnsControlledMissingRuleSettingsAndWorkspaceErrors()
    {
        var workspace = await Workspace(); var other = await Workspace(); var rule = await ValidRule(workspace);
        var noRule = await Add(workspace); var foreign = await Add(other);
        var targeted = await Add(workspace, Request(rule: rule.Id, context: AutomationEventContext.Create("event", null, Payload, out _)));
        using var factory = Factory(workspace); using var client = Client(factory);
        using var noTarget = await client.GetAsync(Preview(noRule.Id)); await Problem(noTarget, HttpStatusCode.Conflict, "automation-rule-required");
        foreach (var id in new[] { foreign.Id, Guid.NewGuid() })
        {
            using var response = await client.GetAsync(Preview(id)); await Problem(response, HttpStatusCode.NotFound, "ResourceNotFound");
        }
        await using var db = Db(); await db.AutomationRuntimeSettings.Where(x => x.WorkspaceId == workspace).ExecuteDeleteAsync();
        using var missing = await client.GetAsync(Preview(targeted.Id)); await Problem(missing, HttpStatusCode.Conflict, "AutomationSettingsMissing");
    }

    [Fact]
    public async Task PreviewDefensivelyRefusesForeignRuleEvenIfStorageWasCorrupted()
    {
        var workspace = await Workspace(); var foreign = await ValidRule(await Workspace()); var job = await Add(workspace);
        await using var db = Db();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            // Test-only corruption: bypass FK triggers to exercise the read boundary's independent workspace filter.
            await db.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AutomationJobs\" SET \"AutomationRuleId\" = {foreign.Id} WHERE \"Id\" = {job.Id}");
            await transaction.CommitAsync();
        }
        using var factory = Factory(workspace); using var client = Client(factory);
        using var response = await client.GetAsync(Preview(job.Id)); await Problem(response, HttpStatusCode.NotFound, "ResourceNotFound");
    }

    private static JsonObject Definition() => new()
    {
        ["name"] = "Rule", ["triggerTypeCode"] = "manual", ["actionTypeCode"] = "create-crm-task",
        ["actionConfigurationJson"] = "{\"title\":\"Relancer\"}", ["conditionJson"] = "{}"
    };

    [Fact]
    public async Task RuleCreateUpdateArchiveRestoreAndLegacyReadPreserveWorkspaceIsolation()
    {
        var workspace = await Workspace(); var other = await Workspace(); var foreignRule = await ValidRule(other);
        using var factory = Factory(workspace); using var client = Client(factory);
        using var created = await client.PostAsJsonAsync("/api/automation-rules", Definition());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var rule = (await created.Content.ReadFromJsonAsync<AutomationRuleDto>())!;
        var update = Definition(); update["name"] = "Updated"; update["conditionJson"] = "{\"type\":\"always\"}";
        using var updated = await client.PutAsJsonAsync($"/api/automation-rules/{rule.Id}", update); Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        foreach (var operation in new[] { "archive", "restore" })
        { using var response = await client.PostAsync($"/api/automation-rules/{rule.Id}/{operation}", null); Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); }
        Assert.Equal("Updated", (await client.GetFromJsonAsync<AutomationRuleDto>($"/api/automation-rules/{rule.Id}"))!.Name);
        using var foreignRead = await client.GetAsync($"/api/automation-rules/{foreignRule.Id}"); Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        using var foreignUpdate = await client.PutAsJsonAsync($"/api/automation-rules/{foreignRule.Id}", update); Assert.Equal(HttpStatusCode.NotFound, foreignUpdate.StatusCode);
        var legacyId = await Rule(workspace);
        var legacy = (await client.GetFromJsonAsync<AutomationRuleDto>($"/api/automation-rules/{legacyId}"))!;
        Assert.Equal("test", legacy.ActionTypeCode);
        var job = await Add(workspace, Request(rule: legacyId, context: AutomationEventContext.Create("legacy", null, Payload, out _)));
        var preview = (await client.GetFromJsonAsync<AutomationEvaluationResult>(Preview(job.Id)))!;
        Assert.False(preview.IsValid); Assert.Equal("unsupported-action", preview.ReasonCode);
        using var archived = await client.PostAsync($"/api/automation-rules/{legacyId}/archive", null); Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        using var restore = await client.PostAsync($"/api/automation-rules/{legacyId}/restore", null); await Problem(restore, HttpStatusCode.BadRequest, "unsupported-action");
        await using var db = Db(); Assert.Equal("test", (await db.AutomationRules.SingleAsync(x => x.Id == legacyId)).ActionTypeCode);
        using var repair = await client.PutAsJsonAsync($"/api/automation-rules/{legacyId}", Definition()); Assert.Equal(HttpStatusCode.NoContent, repair.StatusCode);
        using var repairedRestore = await client.PostAsync($"/api/automation-rules/{legacyId}/restore", null); Assert.Equal(HttpStatusCode.NoContent, repairedRestore.StatusCode);
    }

    [Theory]
    [InlineData("triggerTypeCode", "unknown", "unsupported-trigger")]
    [InlineData("actionTypeCode", "unknown", "unsupported-action")]
    [InlineData("conditionJson", "{\"type\":\"unknown\"}", "invalid-condition")]
    [InlineData("conditionJson", "[]", "invalid-condition")]
    [InlineData("actionConfigurationJson", "{}", "invalid-action-configuration")]
    [InlineData("actionConfigurationJson", null, "invalid-action-configuration")]
    [InlineData("actionConfigurationJson", "{\"title\":\"{{company.name}}\"}", "invalid-action-configuration")]
    [InlineData("workspaceId", "00000000-0000-0000-0000-000000000001", "InvalidRequest")]
    public async Task CreateAndUpdateUseIdenticalStrictValidationWithoutChangingStoredRule(string field, string? value, string code)
    {
        var workspace = await Workspace(); var rule = await ValidRule(workspace);
        using var factory = Factory(workspace); using var client = Client(factory);
        var definition = Definition(); definition[field] = value;
        using var create = await client.PostAsJsonAsync("/api/automation-rules", definition); await Problem(create, HttpStatusCode.BadRequest, code);
        using var update = await client.PutAsJsonAsync($"/api/automation-rules/{rule.Id}", definition); await Problem(update, HttpStatusCode.BadRequest, code);
        await using var db = Db(); var stored = Assert.Single(await db.AutomationRules.Where(x => x.WorkspaceId == workspace).ToListAsync());
        Assert.Equal(rule.Name, stored.Name); Assert.Null(stored.UpdatedAt); Assert.Equal(rule.TriggerTypeCode, stored.TriggerTypeCode);
        Assert.Equal(rule.ActionTypeCode, stored.ActionTypeCode);
    }

    [Fact]
    public async Task RulePipelineOwnershipIsStillRequiredOnCreateAndUpdate()
    {
        var workspace = await Workspace(); var foreign = await Pipeline(await Workspace()); var rule = await ValidRule(workspace);
        using var factory = Factory(workspace); using var client = Client(factory);
        foreach (var pipeline in new[] { foreign, Guid.NewGuid() })
        {
            var definition = Definition(); definition["pipelineId"] = pipeline;
            using var create = await client.PostAsJsonAsync("/api/automation-rules", definition); await Problem(create, HttpStatusCode.BadRequest, "invalid-pipeline");
            using var update = await client.PutAsJsonAsync($"/api/automation-rules/{rule.Id}", definition); await Problem(update, HttpStatusCode.BadRequest, "invalid-pipeline");
        }
    }
}
