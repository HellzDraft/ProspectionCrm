using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProspectionCrm.Api.Dtos.Ingestions;
using ProspectionCrm.Api.Dtos.OpportunitySources;
using ProspectionCrm.Api.Entities;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class OpportunitySourceTests : PersistentSourceIdentityFixture
{
    private WebApplicationFactory<Program> Factory(IInterceptor? interceptor = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing").UseSetting("ConnectionStrings:DefaultConnection", Postgres.GetConnectionString());
            if (interceptor is not null)
                b.ConfigureServices(s => s.AddDbContext<ProspectionCrm.Api.Data.ProspectionCrmDbContext>(o => o.AddInterceptors(interceptor)));
        });
    private static string Route(Setup setup) => $"/api/opportunities/{setup.Opportunity}/sources";
    private static UpdateOpportunitySourceRequest Update(OpportunitySourceDto x) => new()
    {
        SourceLabel = x.SourceLabel, SourceConfigurationId = x.SourceConfigurationId, SavedSearchId = x.SavedSearchId,
        SourceExecutionId = x.SourceExecutionId, SourceUrl = x.SourceUrl, ExternalId = x.ExternalId, LastSeenAt = x.LastSeenAt
    };
    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.DoesNotContain("UX_OpportunitySources", text); Assert.DoesNotContain("23505", text);
    }

    [Fact]
    public async Task ManualIdentitySupportsCrudNormalizationAndProtectsOwnership()
    {
        var setup = await PrepareAsync();
        using var factory = Factory(); using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var route = Route(setup);
        using var response = await client.PostAsJsonAsync(route, new CreateOpportunitySourceRequest
        {
            SourceLabel = "Manual", SourceConfigurationId = setup.Configuration,
            SourceUrl = " HTTPS://EXAMPLE.INVALID/Case?B=2&a=1#Top ", ExternalId = " External "
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var source = (await response.Content.ReadFromJsonAsync<OpportunitySourceDto>())!;
        Assert.Equal(setup.Workspace, source.WorkspaceId);
        Assert.Equal("https://example.invalid/Case?B=2&a=1#Top", source.NormalizedSourceUrl);
        Assert.Equal(" External ", source.ExternalId);
        Assert.Equal(JsonSerializer.Serialize(source),
            JsonSerializer.Serialize(await client.GetFromJsonAsync<OpportunitySourceDto>(response.Headers.Location)));
        Assert.Single((await client.GetFromJsonAsync<OpportunitySourceDto[]>(route))!);
        var update = Update(source); update.SourceUrl = "https://example.invalid/Other"; update.ExternalId = "Other";
        using var changed = await client.PutAsJsonAsync($"{route}/{source.Id}", update);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        var persisted = (await client.GetFromJsonAsync<OpportunitySourceDto>($"{route}/{source.Id}"))!;
        Assert.Equal(update.SourceUrl, persisted.NormalizedSourceUrl);
        using var ownership = await client.PutAsJsonAsync($"{route}/{source.Id}", new
        {
            workspaceId = Guid.NewGuid(), opportunityId = Guid.NewGuid(), sourceLabel = persisted.SourceLabel,
            sourceConfigurationId = persisted.SourceConfigurationId, sourceUrl = persisted.SourceUrl, externalId = persisted.ExternalId
        });
        Assert.Equal(HttpStatusCode.NoContent, ownership.StatusCode);
        using var deleted = await client.DeleteAsync($"{route}/{source.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<OpportunitySourceDto[]>(route))!);
    }

    [Theory]
    [InlineData("invalid-url", "InvalidSourceUrl", 400)]
    [InlineData("empty", "MissingSourceIdentity", 400)]
    [InlineData("external-without-source", "InvalidReference", 400)]
    [InlineData("foreign", "InvalidReference", 400)]
    [InlineData("external", "DuplicateExternalId", 409)]
    [InlineData("url", "DuplicateSourceUrl", 409)]
    [InlineData("dates", "InvalidTimestamps", 400)]
    public async Task InvalidAndConflictingPostReturnsControlledProblem(string kind, string code, int status)
    {
        var setup = await PrepareAsync();
        await IngestAsync(setup, Item());
        await using var db = Db();
        var foreign = await SeedAsync(db, archived: true);
        using var factory = Factory(); using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var request = new CreateOpportunitySourceRequest { SourceLabel = "Manual", SourceConfigurationId = setup.Configuration };
        switch (kind)
        {
            case "invalid-url": request.SourceUrl = "https://user:secret@example.invalid"; break;
            case "external-without-source": request.ExternalId = "new"; request.SourceConfigurationId = null; break;
            case "foreign": request.SourceUrl = "https://example.invalid/new"; request.SourceConfigurationId = foreign.Configuration; break;
            case "external": request.ExternalId = "one"; break;
            case "url": request.SourceUrl = " HTTPS://EXAMPLE.INVALID/Job "; break;
            case "dates": request.ExternalId = "new"; request.FirstSeenAt = DateTimeOffset.UtcNow; request.LastSeenAt = request.FirstSeenAt.Value.AddDays(-1); break;
        }
        using var response = await client.PostAsJsonAsync(Route(setup), request);
        await ProblemAsync(response, (HttpStatusCode)status, code);
        Assert.Equal(1, await db.OpportunitySources.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedIdentityIsImmutableButExactPutIsNoOp(bool createdByIngestion)
    {
        var setup = await PrepareAsync();
        using var factory = Factory(); using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        if (!createdByIngestion)
        {
            using var create = await client.PostAsJsonAsync(Route(setup), new CreateOpportunitySourceRequest
                { SourceLabel = "Manual", SourceUrl = "https://example.invalid/Job" });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }
        var ingested = (await IngestAsync(setup, Item(null))).Value!;
        var route = $"/api/opportunities/{ingested.Items[0].OpportunityId}/sources";
        var source = Assert.Single((await client.GetFromJsonAsync<OpportunitySourceDto[]>(route))!);
        Assert.Equal(createdByIngestion, source.SourceExecutionId.HasValue);
        using var noop = await client.PutAsJsonAsync($"{route}/{source.Id}", Update(source));
        Assert.Equal(HttpStatusCode.NoContent, noop.StatusCode);
        foreach (var field in new[] { "configuration", "search", "execution", "label", "url", "external", "seen" })
        {
            var update = Update(source);
            switch (field)
            {
                case "configuration": update.SourceConfigurationId = Guid.NewGuid(); break;
                case "search": update.SavedSearchId = Guid.NewGuid(); break;
                case "execution": update.SourceExecutionId = Guid.NewGuid(); break;
                case "label": update.SourceLabel = "Changed"; break;
                case "url": update.SourceUrl = "https://example.invalid/other"; break;
                case "external": update.ExternalId = "changed"; break;
                case "seen": update.LastSeenAt = source.LastSeenAt!.Value.AddDays(1); break;
            }
            using var changed = await client.PutAsJsonAsync($"{route}/{source.Id}", update);
            await ProblemAsync(changed, HttpStatusCode.Conflict, "ImmutableSourceIdentity");
        }
        using var deleted = await client.DeleteAsync($"{route}/{source.Id}");
        await ProblemAsync(deleted, HttpStatusCode.Conflict, "SourceIdentityInUse");
        Assert.Equal(JsonSerializer.Serialize(source),
            JsonSerializer.Serialize(await client.GetFromJsonAsync<OpportunitySourceDto>($"{route}/{source.Id}")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalSqlUniqueRaceBecomesControlled409(bool external)
    {
        var setup = await PrepareAsync();
        var gate = new PersistentSourceIdentityGate(db => db.ChangeTracker.Entries<OpportunitySource>().Any(x => x.State == EntityState.Added));
        using var factory = Factory(gate); using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = client.PostAsJsonAsync(Route(setup), new CreateOpportunitySourceRequest
        {
            SourceLabel = "Racing", SourceConfigurationId = setup.Configuration, ExternalId = external ? "same" : null,
            SourceUrl = external ? null : "HTTPS://EXAMPLE.INVALID/Race"
        }, timeout.Token);
        try
        {
            await gate.Reached.Task.WaitAsync(timeout.Token);
            await using var sqlWriter = Db();
            sqlWriter.OpportunitySources.Add(new OpportunitySource
            {
                WorkspaceId = setup.Workspace, OpportunityId = setup.Opportunity, SourceConfigurationId = setup.Configuration,
                SourceLabel = "External writer", ExternalId = external ? "same" : null,
                SourceUrl = external ? null : "https://example.invalid/Race", NormalizedSourceUrl = external ? null : "https://example.invalid/Race"
            });
            await sqlWriter.SaveChangesAsync(timeout.Token);
        }
        finally { gate.Release.TrySetResult(); }
        using var response = await pending;
        await ProblemAsync(response, HttpStatusCode.Conflict, external ? "DuplicateExternalId" : "DuplicateSourceUrl");
        await using var verify = Db(); Assert.Equal(1, await verify.OpportunitySources.CountAsync());
    }

    [Fact]
    public async Task SourceExecutionReferenceAloneProtectsIdentityAndInfersReferences()
    {
        var setup = await PrepareAsync();
        using var factory = Factory(); using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var response = await client.PostAsJsonAsync(Route(setup), new CreateOpportunitySourceRequest
            { SourceLabel = "Origin", SourceExecutionId = setup.Execution, ExternalId = "id" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var source = (await response.Content.ReadFromJsonAsync<OpportunitySourceDto>())!;
        Assert.Equal(setup.Configuration, source.SourceConfigurationId); Assert.Equal(setup.Search, source.SavedSearchId);
        await using var db = Db(); Assert.Empty(await db.SourceExecutionItemSources.ToArrayAsync());
        using var deleted = await client.DeleteAsync($"{Route(setup)}/{source.Id}");
        await ProblemAsync(deleted, HttpStatusCode.Conflict, "SourceIdentityInUse");
    }

    [Theory]
    [InlineData("url", "DuplicateSourceUrl", 409)]
    [InlineData("external", "DuplicateExternalId", 409)]
    [InlineData("invalid", "InvalidSourceUrl", 400)]
    [InlineData("empty", "MissingSourceIdentity", 400)]
    public async Task UnusedIdentityUpdateChecksNewValuesWithoutPartialMutation(string kind, string code, int status)
    {
        var setup = await PrepareAsync();
        await IngestAsync(setup, Item());
        using var factory = Factory(); using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var create = await client.PostAsJsonAsync(Route(setup), new CreateOpportunitySourceRequest
            { SourceLabel = "Unused", SourceConfigurationId = setup.Configuration, SourceUrl = "https://example.invalid/unused", ExternalId = "unused" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var source = (await create.Content.ReadFromJsonAsync<OpportunitySourceDto>())!;
        var update = Update(source);
        if (kind == "url") update.SourceUrl = " HTTPS://EXAMPLE.INVALID/Job ";
        if (kind == "external") update.ExternalId = "one";
        if (kind == "invalid") update.SourceUrl = "/relative";
        if (kind == "empty") { update.ExternalId = null; update.SourceUrl = null; }
        using var changed = await client.PutAsJsonAsync($"{Route(setup)}/{source.Id}", update);
        await ProblemAsync(changed, (HttpStatusCode)status, code);
        Assert.Equal(JsonSerializer.Serialize(source), JsonSerializer.Serialize(
            await client.GetFromJsonAsync<OpportunitySourceDto>($"{Route(setup)}/{source.Id}")));
    }

    [Fact]
    public async Task PostWinningLockIsRediscoveredByWaitingHttpIngestion()
    {
        var setup = await PrepareAsync();
        var gate = new PersistentSourceIdentityGate(db => db.ChangeTracker.Entries<OpportunitySource>().Any(x => x.State == EntityState.Added));
        using var factory = Factory(gate); using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token = timeout.Token;
        var post = client.PostAsJsonAsync(Route(setup), new CreateOpportunitySourceRequest
            { SourceLabel = "First", SourceUrl = "HTTPS://EXAMPLE.INVALID/Job" }, token);
        await gate.Reached.Task.WaitAsync(token);
        var ingestion = client.PostAsJsonAsync($"/api/saved-searches/{setup.Search}/ingestions",
            new IngestionRequest { PipelineStageId = setup.Stage, Items = [Item()] }, token);
        try
        {
            await using var observer = new NpgsqlConnection(Postgres.GetConnectionString()); await observer.OpenAsync(token);
            await using var waiting = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()
                AND wait_event_type = 'Lock' AND query LIKE '%pg_advisory_xact_lock%'
                """, observer);
            while (Convert.ToInt64(await waiting.ExecuteScalarAsync(token)) == 0)
            {
                Assert.False(ingestion.IsCompleted);
                await Task.Delay(25, token);
            }
        }
        finally { gate.Release.TrySetResult(); }
        using var created = await post; Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var rediscovered = await ingestion; Assert.Equal(HttpStatusCode.Created, rediscovered.StatusCode);
        var result = (await rediscovered.Content.ReadFromJsonAsync<IngestionDto>())!;
        Assert.Equal(setup.Opportunity, result.Items[0].OpportunityId);
        Assert.Equal(1, result.Execution.ItemsUpdated);
        await using var db = Db(); Assert.Equal(1, await db.Opportunities.CountAsync());
        Assert.Equal(1, await db.OpportunitySources.CountAsync(x => x.NormalizedSourceUrl != null));
    }
}
