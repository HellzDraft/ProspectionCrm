using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class Phase7ApiErrorTests : PersistentSourceIdentityFixture
{
    private const string Sensitive = "phase7-audit-sensitive-marker";
    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseEnvironment("Development").ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = Postgres.GetConnectionString(),
            ["SourceCollectionWorker:Enabled"] = "false", ["SourceCollectionScheduler:Enabled"] = "false"
        })));
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { BaseAddress = new("https://localhost") });
    private static async Task Controlled(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Sensitive, body);
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", body);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ScheduleUnknownPropertyDoesNotEchoRejectedSensitiveInput()
    {
        var setup = await PrepareAsync(); using var factory = Factory(); using var client = Client(factory);
        using var response = await client.PutAsync($"/api/saved-searches/{setup.Search}/schedule",
            new StringContent("{\"enabled\":false,\"" + Sensitive + "\":true}", Encoding.UTF8, "application/json"));
        await Controlled(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ScheduleWithoutActiveWorkspaceReturnsControlledValidationError()
    {
        var setup = await PrepareAsync(); await using var db = Db();
        await db.Workspaces.ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow));
        using var factory = Factory(); using var client = Client(factory);
        using var response = await client.PutAsJsonAsync($"/api/saved-searches/{setup.Search}/schedule", new { enabled = false });
        await Controlled(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DatabaseFailureDoesNotExposeSqlOrStackTrace(bool schedule)
    {
        var setup = await PrepareAsync(); await using var db = Db();
        await db.SourceConfigurations.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceTypeCode, "rss"));
        await db.SavedSearches.ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchUrl, "https://feeds.example.org/jobs.xml"));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_phase7_insert() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN RAISE EXCEPTION 'phase7-audit-sensitive-marker SELECT private_data'; END $body$;
            CREATE TRIGGER fail_job BEFORE INSERT ON "SourceCollectionJobs" FOR EACH ROW EXECUTE FUNCTION fail_phase7_insert();
            CREATE TRIGGER fail_schedule BEFORE INSERT ON "SourceCollectionSchedules" FOR EACH ROW EXECUTE FUNCTION fail_phase7_insert();
            """);
        using var factory = Factory(); using var client = Client(factory);
        using var response = schedule
            ? await client.PutAsJsonAsync($"/api/saved-searches/{setup.Search}/schedule", new { enabled = true, dailyUtcTime = "09:30", pipelineStageId = setup.Stage })
            : await client.PostAsJsonAsync($"/api/saved-searches/{setup.Search}/collection-jobs", new { pipelineStageId = setup.Stage });
        await Controlled(response, HttpStatusCode.InternalServerError);
        Assert.Empty(await db.SourceCollectionSchedules.ToListAsync()); Assert.Empty(await db.SourceCollectionJobs.ToListAsync());
    }
}
