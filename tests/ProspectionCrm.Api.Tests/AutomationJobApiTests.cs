using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.AutomationJobs;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationJobApiTests(AutomationJobDatabase database) : AutomationJobFixture(database), IClassFixture<AutomationJobDatabase>
{
    private const string Route = "/api/automation-jobs";

    [Fact]
    public async Task PostUsesCurrentWorkspaceAndReturnsLocationOrExistingJobWithoutLeaseCapability()
    {
        var workspace = await Workspace(); var rule = await Rule(workspace);
        using var factory = Factory(workspace); using var client = Client(factory);
        using var created = await client.PostAsJsonAsync(Route + "?workspaceId=" + Guid.NewGuid(), Request("api-event", rule: rule));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.NotNull(created.Headers.Location);
        var job = (await created.Content.ReadFromJsonAsync<AutomationJobDto>())!;
        Assert.Equal(workspace, job.WorkspaceId); Assert.Equal(rule, job.AutomationRuleId);
        using var duplicate = await client.PostAsJsonAsync(Route, Request("api-event"));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode); Assert.Equal(job.Id, (await duplicate.Content.ReadFromJsonAsync<AutomationJobDto>())!.Id);
        await using var db = Db(); await Queue(db).ClaimAsync(workspace, default);
        using var get = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode); var json = JsonNode.Parse(await get.Content.ReadAsStringAsync())!.AsObject();
        Assert.False(json.ContainsKey("leaseOwner")); Assert.NotNull(json["leaseExpiresAt"]);
        Assert.Empty(await db.AutomationExecutions.Where(x => x.WorkspaceId == workspace).ToListAsync());
        foreach (var operation in new[] { "claim", "complete", "fail", "cancel", "release", "recover" })
        {
            using var response = await client.PostAsync($"{Route}/{job.Id}/{operation}", null);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task RulesAndReadsAreIsolatedAndListFiltersAndPaginationAreDeterministic()
    {
        var workspace = await Workspace(); var other = await Workspace(); var rule = await Rule(workspace); var foreignRule = await Rule(other);
        var foreign = await Add(other); var email = await Add(workspace, Request(rule: rule, category: "email"));
        var general = await Add(workspace); await using var db = Db(); await Queue(db).CancelAsync(workspace, general.Id, default);
        using var factory = Factory(workspace); using var client = Client(factory);
        foreach (var id in new[] { foreignRule, Guid.NewGuid() })
        {
            using var response = await client.PostAsJsonAsync(Route, Request(rule: id));
            await Problem(response, HttpStatusCode.NotFound, "AutomationRuleNotFound");
        }
        foreach (var id in new[] { foreign.Id, Guid.NewGuid() })
        {
            using var response = await client.GetAsync($"{Route}/{id}"); await Problem(response, HttpStatusCode.NotFound, "ResourceNotFound");
        }
        var page = (await client.GetFromJsonAsync<AutomationJobsPageDto>(Route + "?limit=1"))!;
        Assert.Equal(2, page.Total); Assert.True(page.HasMore); Assert.Equal(general.Id, Assert.Single(page.Items).Id);
        var next = (await client.GetFromJsonAsync<AutomationJobsPageDto>(Route + "?limit=1&offset=1"))!;
        Assert.False(next.HasMore); Assert.Equal(email.Id, Assert.Single(next.Items).Id);
        foreach (var filter in new[] { "status=pending", $"automationRuleId={rule}", "actionCategory=email",
            $"status=pending&automationRuleId={rule}&triggerType=manual&actionCategory=email" })
            Assert.Equal(email.Id, Assert.Single((await client.GetFromJsonAsync<AutomationJobsPageDto>(Route + "?" + filter))!.Items).Id);
        Assert.Equal(2, (await client.GetFromJsonAsync<AutomationJobsPageDto>(Route + "?triggerType=manual"))!.Total);
        Assert.Empty((await client.GetFromJsonAsync<AutomationJobsPageDto>(Route + $"?automationRuleId={foreignRule}"))!.Items);
        using var otherFactory = Factory(other); using var otherClient = Client(otherFactory);
        Assert.Equal(foreign.Id, Assert.Single((await otherClient.GetFromJsonAsync<AutomationJobsPageDto>(Route))!.Items).Id);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{bad-secret-marker}")]
    [InlineData("{\"triggerTypeCode\":\"manual\",\"actionCategoryCode\":\"general\",\"workspaceId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"triggerTypeCode\":\"manual\",\"actionCategoryCode\":\"general\",\"extra\":\"secret-marker\"}")]
    [InlineData("{\"triggerTypeCode\":\"manual\",\"actionCategoryCode\":\"general\",\"availableAt\":\"secret-marker\"}")]
    [InlineData("{\"triggerTypeCode\":\"manual\",\"actionCategoryCode\":\"general\",\"priority\":\"secret-marker\"}")]
    [InlineData("{\"triggerTypeCode\":null,\"actionCategoryCode\":\"general\"}")]
    public async Task BindingRejectsUnknownFieldsAndMalformedInputWithoutEcho(string json)
    {
        var workspace = await Workspace(); using var factory = Factory(workspace); using var client = Client(factory);
        using var response = await client.PostAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"));
        await Problem(response, HttpStatusCode.BadRequest, "InvalidRequest");
    }

    [Theory]
    [InlineData("triggerTypeCode", "secret-marker", "InvalidTriggerTypeCode")]
    [InlineData("actionCategoryCode", "secret-marker", "InvalidActionCategoryCode")]
    [InlineData("contextJson", "{secret-marker", "InvalidContextJson")]
    [InlineData("contextJson", "[]", "InvalidContextJson")]
    [InlineData("triggerKey", " ", "InvalidTriggerKey")]
    public async Task SemanticValidationReturnsProblemDetails(string field, string value, string code)
    {
        var workspace = await Workspace(); using var factory = Factory(workspace); using var client = Client(factory);
        var body = new JsonObject { ["triggerTypeCode"] = "manual", ["actionCategoryCode"] = "general", [field] = value };
        using var response = await client.PostAsJsonAsync(Route, body); await Problem(response, HttpStatusCode.BadRequest, code);
    }

    [Theory]
    [InlineData("offset=-1", "InvalidPagination")]
    [InlineData("limit=201", "InvalidPagination")]
    [InlineData("limit=0", "InvalidPagination")]
    [InlineData("status=secret-marker", "InvalidStatusCode")]
    [InlineData("triggerType=secret-marker", "InvalidTriggerTypeCode")]
    [InlineData("actionCategory=secret-marker", "InvalidActionCategoryCode")]
    [InlineData("automationRuleId=secret-marker", "InvalidRequest")]
    public async Task InvalidFiltersAreControlled(string query, string code)
    {
        using var factory = Factory(await Workspace()); using var client = Client(factory);
        using var response = await client.GetAsync(Route + "?" + query); await Problem(response, HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task PriorityBoundsContextSizeAndPastTimestampsAreValidatedThroughHttp()
    {
        using var factory = Factory(await Workspace()); using var client = Client(factory);
        foreach (var priority in new[] { -1, 101 })
        {
            using var response = await client.PostAsJsonAsync(Route, Request(priority: priority));
            await Problem(response, HttpStatusCode.BadRequest, "InvalidRequest");
        }
        using var large = await client.PostAsJsonAsync(Route, Request(context: "{\"x\":\"" + new string('x', 17000) + "\"}"));
        await Problem(large, HttpStatusCode.BadRequest, "InvalidContextJson");
        foreach (var priority in new[] { 0, 100 })
        {
            using var response = await client.PostAsJsonAsync(Route, Request(priority: priority, available: DateTimeOffset.UtcNow.AddDays(-1)));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task AmbiguousWorkspaceReturnsConflict()
    {
        await Workspace(); await Workspace(); using var factory = Factory(null); using var client = Client(factory);
        using var list = await client.GetAsync(Route); await Problem(list, HttpStatusCode.Conflict, "WorkspaceUnavailable");
        using var post = await client.PostAsJsonAsync(Route, Request()); await Problem(post, HttpStatusCode.Conflict, "WorkspaceUnavailable");
    }

    [Fact]
    public async Task UnexpectedStorageFailureHasSanitizedProblemDetails()
    {
        var workspace = await Workspace(); await using var db = Db();
        // Trigger scoped to this test workspace; other test data in the class is unaffected.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION reject_automation_job_test() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN IF NEW."TriggerKey" = 'storage-failure-test' THEN RAISE EXCEPTION 'secret-marker SELECT private_data'; END IF; RETURN NEW; END $body$;
            CREATE TRIGGER reject_automation_job_test BEFORE INSERT ON "AutomationJobs" FOR EACH ROW EXECUTE FUNCTION reject_automation_job_test();
            """);
        using var factory = Factory(workspace); using var client = Client(factory);
        using var response = await client.PostAsJsonAsync(Route, Request("storage-failure-test"));
        await Problem(response, HttpStatusCode.InternalServerError, "AutomationJobInternalError");
        Assert.Empty(await db.AutomationJobs.Where(x => x.WorkspaceId == workspace).ToListAsync());
    }

    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var text = await response.Content.ReadAsStringAsync(); var json = JsonNode.Parse(text)!;
        Assert.Equal((int)status, (int)json["status"]!); Assert.Equal(code, (string?)json["code"]);
        Assert.NotNull(json["title"]); Assert.DoesNotContain("secret-marker", text); Assert.DoesNotContain("SELECT", text); Assert.DoesNotContain("Exception", text);
    }
}
