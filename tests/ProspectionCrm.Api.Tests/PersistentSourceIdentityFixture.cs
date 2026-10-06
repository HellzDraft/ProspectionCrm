using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Ingestions;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public abstract class PersistentSourceIdentityFixture : IAsyncLifetime
{
    protected const string Previous = "20261006091016_Phase621IngestionHistoryFoundation";
    protected readonly PostgreSqlContainer Postgres = new PostgreSqlBuilder("postgres:18").Build();
    public Task InitializeAsync() => Postgres.StartAsync();
    public Task DisposeAsync() => Postgres.DisposeAsync().AsTask();
    protected ProspectionCrmDbContext Db(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<ProspectionCrmDbContext>().UseNpgsql(Postgres.GetConnectionString()).AddInterceptors(interceptors).Options);
    protected sealed record Setup(Guid Workspace, Guid Pipeline, Guid Stage, Guid Configuration, Guid Search, Guid Execution, Guid Opportunity);
    protected async Task<Setup> PrepareAsync(string? migration = null)
    {
        await using var db = Db();
        await db.GetService<IMigrator>().MigrateAsync(migration);
        return await SeedAsync(db);
    }
    protected static async Task<Setup> SeedAsync(ProspectionCrmDbContext db, bool archived = false)
    {
        var workspace = new Workspace { Name = "Identity", TimeZoneId = "UTC", ArchivedAt = archived ? DateTimeOffset.UtcNow : null,
            OwnerUser = new UserAccount { Email = $"{Guid.NewGuid():N}@example.invalid" } };
        var pipeline = new Pipeline { Workspace = workspace, Name = "Pipeline", TypeCode = "custom" };
        var stage = new PipelineStage { Pipeline = pipeline, Name = "Stage", CategoryCode = "active" };
        var config = new SourceConfiguration { Workspace = workspace, Name = "Source", SourceTypeCode = "manual" };
        var search = new SavedSearch { Workspace = workspace, Pipeline = pipeline, SourceConfiguration = config, Name = "Search" };
        var execution = new SourceExecution { Workspace = workspace, SourceConfiguration = config, SavedSearch = search,
            TriggerTypeCode = "manual", StatusCode = "succeeded" };
        var opportunity = new Opportunity { Workspace = workspace, PipelineStage = stage, Title = "Manual", PriorityCode = "normal" };
        db.SourceExecutions.Add(execution);
        db.Opportunities.Add(opportunity);
        await db.SaveChangesAsync();
        return new(workspace.Id, pipeline.Id, stage.Id, config.Id, search.Id, execution.Id, opportunity.Id);
    }
    protected static IngestionItemRequest Item(string? external = "one", string? url = "https://example.invalid/Job",
        string title = "Unity Developer", string? company = null) =>
        new() { ExternalId = external, SourceUrl = url, Title = title, CompanyName = company };
    protected async Task<IngestionResult> IngestAsync(Setup setup, params IngestionItemRequest[] items)
    {
        await using var db = Db();
        return await new IngestionService(db, new FixedWorkspace(setup.Workspace), NullLogger<IngestionService>.Instance)
            .IngestAsync(setup.Search, new() { PipelineStageId = setup.Stage, Items = items.ToList() }, default);
    }
    protected sealed class FixedWorkspace(Guid workspace) : ICurrentWorkspaceProvider
    {
        public Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(workspace);
    }
    protected static async Task<Guid> LegacySourceAsync(ProspectionCrmDbContext db, Setup setup, string? url,
        Guid? configuration = null, Guid? search = null, Guid? execution = null, string? external = null)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OpportunitySources" ("Id", "OpportunityId", "SourceConfigurationId", "SavedSearchId",
                "SourceExecutionId", "SourceLabel", "SourceUrl", "ExternalId", "FirstSeenAt")
            VALUES ({id}, {setup.Opportunity}, {configuration}, {search}, {execution}, 'Legacy', {url}, {external}, now())
            """);
        return id;
    }
}
