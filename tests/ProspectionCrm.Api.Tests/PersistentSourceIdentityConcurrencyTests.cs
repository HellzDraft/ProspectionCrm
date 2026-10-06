using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ProspectionCrm.Api.Dtos.Companies;
using ProspectionCrm.Api.Dtos.Opportunities;
using ProspectionCrm.Api.Dtos.OpportunitySources;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class PersistentSourceIdentityConcurrencyTests : PersistentSourceIdentityFixture
{
    [Theory]
    [InlineData("post-source")]
    [InlineData("put-opportunity")]
    [InlineData("put-company")]
    [InlineData("delete-opportunity")]
    [InlineData("delete-company")]
    public async Task IngestionHoldsIdentityDecisionUntilCommitAgainstCooperatingHttpWrites(string operation)
    {
        var setup = await PrepareAsync();
        await using var seed = Db();
        var company = new Company { WorkspaceId = setup.Workspace, Name = "Studio" };
        seed.Companies.Add(company);
        var opportunity = await seed.Opportunities.SingleAsync();
        opportunity.Title = "Unity Developer"; opportunity.Company = company;
        await seed.SaveChangesAsync();
        var gate = new PersistentSourceIdentityGate(db => db.ChangeTracker.Entries<SourceExecutionItem>()
            .Any(x => x.Entity.OutcomeCode == "updated"));
        await using var ingestionDb = Db(gate);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var ingestion = new IngestionService(ingestionDb, new FixedWorkspace(setup.Workspace), NullLogger<IngestionService>.Instance)
            .IngestAsync(setup.Search, new() { PipelineStageId = setup.Stage, Items = [Item(company: "Studio")] }, token);
        await gate.Reached.Task.WaitAsync(token);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:DefaultConnection", Postgres.GetConnectionString()));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var write = operation switch
        {
            "post-source" => client.PostAsJsonAsync($"/api/opportunities/{setup.Opportunity}/sources",
                new CreateOpportunitySourceRequest { SourceLabel = "Duplicate", SourceUrl = "HTTPS://EXAMPLE.INVALID/Job" }, token),
            "put-opportunity" => client.PutAsJsonAsync($"/api/opportunities/{setup.Opportunity}",
                new UpdateOpportunityRequest { Title = "Changed", CompanyId = null, PipelineStageId = setup.Stage, PriorityCode = "normal" }, token),
            "put-company" => client.PutAsJsonAsync($"/api/companies/{company.Id}", new UpdateCompanyRequest { Name = "Changed" }, token),
            "delete-company" => client.DeleteAsync($"/api/companies/{company.Id}", token),
            _ => client.DeleteAsync($"/api/opportunities/{setup.Opportunity}", token)
        };
        try
        {
            await using var observer = new NpgsqlConnection(Postgres.GetConnectionString()); await observer.OpenAsync(token);
            await using var waiting = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()
                AND wait_event_type = 'Lock' AND query LIKE '%pg_advisory_xact_lock%'
                """, observer);
            while (Convert.ToInt64(await waiting.ExecuteScalarAsync(token)) == 0)
            {
                Assert.False(write.IsCompleted);
                await Task.Delay(25, token);
            }
            Assert.False(write.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(IngestionStatus.Succeeded, (await ingestion).Status);
        using var response = await write;
        Assert.Equal(operation == "post-source" ? HttpStatusCode.Conflict : HttpStatusCode.NoContent, response.StatusCode);
        await using var verify = Db();
        var item = await verify.SourceExecutionItems.SingleAsync();
        Assert.Equal(setup.Opportunity, item.OpportunityIdSnapshot);
        if (operation == "delete-opportunity") Assert.Null(item.OpportunityId);
        else Assert.Equal(setup.Opportunity, item.OpportunityId);
        Assert.Equal(operation == "delete-opportunity" ? 0 : 1, await verify.OpportunitySources.CountAsync());
    }

    [Fact]
    public async Task SharedLockRequiresTransactionAndKeepsV1Key()
    {
        await using var db = Db();
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceIdentityLock.AcquireAsync(db, Guid.Empty, default));
        var id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var expected = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"ProspectionCrm/manual-ingestion/{id:D}")));
        Assert.Equal(expected, WorkspaceIdentityLock.Key(id));
    }
}
