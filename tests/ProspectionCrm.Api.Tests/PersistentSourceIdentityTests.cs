using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class PersistentSourceIdentityTests : PersistentSourceIdentityFixture
{
    [Fact]
    public async Task DurableAndSameBatchFallbackDoNotCreateEmptySources()
    {
        var setup = await PrepareAsync();
        var result = await IngestAsync(setup, Item(company: " Studio A "), Item(null, null, "UNITY  DEVELOPER", "studio a"));
        Assert.Equal(IngestionStatus.Succeeded, result.Status);
        Assert.Equal(new[] { "created", "updated" }, result.Value!.Items.Select(x => x.Outcome));
        Assert.Single(result.Value.Items.Select(x => x.OpportunityId).Distinct());
        var id = result.Value.Items[0].OpportunityId;
        await using var db = Db();
        var source = await db.OpportunitySources.SingleAsync();
        Assert.Equal(setup.Workspace, source.WorkspaceId);
        Assert.Equal(IngestionNormalization.UrlKey(source.SourceUrl), source.NormalizedSourceUrl);
        Assert.Null((await db.Opportunities.SingleAsync(x => x.Id == id)).CompanyId);
        // A user edit/archive cannot erase the historical identity or be overwritten by replay.
        await db.Opportunities.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, "User title")
            .SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow));
        var replay = await IngestAsync(setup, Item(null, null, company: "Studio A"));
        Assert.Equal(id, replay.Value!.Items[0].OpportunityId);
        var history = await db.SourceExecutionItems.SingleAsync(x => x.SourceExecutionId == replay.Value.Execution.Id);
        Assert.Equal(IngestionHistoryCodes.MatchedTitleCompany, history.DecisionCode);
        Assert.Empty(await db.SourceExecutionItemSources.Where(x => x.SourceExecutionItemId == history.Id).ToArrayAsync());
        Assert.Equal(1, await db.OpportunitySources.CountAsync());
        Assert.Equal("User title", (await db.Opportunities.AsNoTracking().SingleAsync(x => x.Id == id)).Title);
        await db.Opportunities.Where(x => x.Id == id).ExecuteDeleteAsync();
        Assert.All(await db.SourceExecutionItems.AsNoTracking().ToArrayAsync(), x => Assert.Null(x.OpportunityId));
        var deleted = await IngestAsync(setup, Item(null, null, company: "Studio A"));
        Assert.Equal(IngestionErrorCode.MissingPersistentIdentity, deleted.Error!.Code);
    }

    [Fact]
    public async Task HistoricalAndCurrentBusinessCandidatesAreCombinedWithoutArbitraryChoice()
    {
        var setup = await PrepareAsync();
        var first = (await IngestAsync(setup, Item(company: "Studio"))).Value!;
        await using var db = Db();
        var other = new Opportunity { WorkspaceId = setup.Workspace, PipelineStageId = setup.Stage,
            Title = "Unity Developer", PriorityCode = "normal",
            Company = new Company { WorkspaceId = setup.Workspace, Name = "Studio" } };
        db.Opportunities.Add(other); await db.SaveChangesAsync();
        var conflict = await IngestAsync(setup, Item("one", company: "Studio"));
        Assert.Equal(IngestionErrorCode.AmbiguousIdentity, conflict.Error!.Code);
        Assert.Equal(1, await db.OpportunitySources.CountAsync());
        // Import a legitimate pre-6.2.2 successful observation of the second opportunity.
        var prior = await db.SourceExecutionItems.AsNoTracking().SingleAsync(x => x.SourceExecutionId == first.Execution.Id);
        prior.Id = Guid.NewGuid(); prior.SourceExecutionId = setup.Execution; prior.ItemIndex = 0;
        prior.OpportunityId = prior.OpportunityIdSnapshot = other.Id;
        db.SourceExecutionItems.Add(prior); await db.SaveChangesAsync();
        await db.Opportunities.Where(x => x.Id == other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompanyId, (Guid?)null));
        var ambiguousHistory = await IngestAsync(setup, Item(null, null, company: "Studio"));
        Assert.Equal(IngestionErrorCode.AmbiguousIdentity, ambiguousHistory.Error!.Code);
        Assert.Equal(0, (await db.SourceExecutions.SingleAsync(x => x.Id == ambiguousHistory.Error.ExecutionId)).ItemsCreated);
    }

    [Fact]
    public async Task LegacyEmptyProvenanceSurvivesAndIsNotObservedOrDuplicated()
    {
        var setup = await PrepareAsync(Previous);
        await using var db = Db();
        var company = new Company { WorkspaceId = setup.Workspace, Name = "Studio" };
        db.Companies.Add(company);
        var opportunity = await db.Opportunities.SingleAsync();
        opportunity.Title = "Unity Developer"; opportunity.Company = company;
        await db.SaveChangesAsync();
        var id = await LegacySourceAsync(db, setup, null, setup.Configuration);
        await db.Database.MigrateAsync();
        var before = await db.OpportunitySources.AsNoTracking().SingleAsync();
        var result = await IngestAsync(setup, Item(null, null, company: "Studio"), Item(null, null, company: "Studio"));
        Assert.Equal(IngestionStatus.Succeeded, result.Status);
        var after = await db.OpportunitySources.AsNoTracking().SingleAsync();
        Assert.Equal(id, after.Id);
        Assert.Equal(before.LastSeenAt, after.LastSeenAt);
        Assert.Null(after.ExternalId); Assert.Null(after.SourceUrl);
        Assert.Empty(await db.SourceExecutionItemSources.ToArrayAsync());
    }

    [Fact]
    public async Task PersistedIdentitiesHaveWorkspaceAndConfigurationScopes()
    {
        var setup = await PrepareAsync();
        var first = (await IngestAsync(setup, Item())).Value!;
        Assert.Equal(first.Items[0].OpportunityId, (await IngestAsync(setup, Item(url: null))).Value!.Items[0].OpportunityId);
        Assert.Equal(first.Items[0].OpportunityId, (await IngestAsync(setup, Item(null, "HTTPS://EXAMPLE.INVALID/Job"))).Value!.Items[0].OpportunityId);
        await using var db = Db();
        var other = await SeedAsync(db, archived: true);
        await db.Workspaces.Where(x => x.Id == other.Workspace).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, (DateTimeOffset?)null));
        var foreign = await IngestAsync(other, Item());
        Assert.Equal(IngestionStatus.Succeeded, foreign.Status);
        Assert.NotEqual(first.Items[0].OpportunityId, foreign.Value!.Items[0].OpportunityId);
        var second = new SourceConfiguration { WorkspaceId = setup.Workspace, Name = "Other", SourceTypeCode = "manual" };
        db.SourceConfigurations.Add(second); await db.SaveChangesAsync();
        await db.SavedSearches.Where(x => x.Id == setup.Search).ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceConfigurationId, second.Id));
        var ownOther = await IngestAsync(setup, Item(url: null));
        Assert.NotEqual(first.Items[0].OpportunityId, ownOther.Value!.Items[0].OpportunityId);
        // Same normalized URL via a third configuration attaches only a new external identity.
        var third = new SourceConfiguration { WorkspaceId = setup.Workspace, Name = "Third", SourceTypeCode = "manual" };
        db.SourceConfigurations.Add(third); await db.SaveChangesAsync();
        await db.SavedSearches.Where(x => x.Id == setup.Search).ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceConfigurationId, third.Id));
        var shared = await IngestAsync(setup, Item());
        Assert.Equal(first.Items[0].OpportunityId, shared.Value!.Items[0].OpportunityId);
        Assert.Equal(1, await db.OpportunitySources.CountAsync(x => x.WorkspaceId == setup.Workspace && x.NormalizedSourceUrl != null));
    }

    [Fact]
    public async Task PostgreSqlEnforcesCompositeReferencesIdentityUniquenessAndUrlPairs()
    {
        var setup = await PrepareAsync();
        await IngestAsync(setup, Item());
        await using var db = Db();
        var other = await SeedAsync(db, archived: true);
        var sourceId = await db.OpportunitySources.Select(x => x.Id).SingleAsync();
        foreach (var change in new (string Column, Guid Value)[]
        {
            ("WorkspaceId", other.Workspace), ("OpportunityId", other.Opportunity), ("SourceConfigurationId", other.Configuration),
            ("SavedSearchId", other.Search), ("SourceExecutionId", other.Execution)
        })
        {
            await using var connection = new NpgsqlConnection(Postgres.GetConnectionString()); await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"""UPDATE "OpportunitySources" SET "{change.Column}" = @value WHERE "Id" = @id""", connection);
            command.Parameters.AddWithValue("value", change.Value); command.Parameters.AddWithValue("id", sourceId);
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
        foreach (var column in new[] { "SourceUrl", "NormalizedSourceUrl", "SourceConfigurationId" })
        {
            var sql = $"""UPDATE "OpportunitySources" SET "{column}" = NULL""";
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        foreach (var urlIdentity in new[] { true, false })
        {
            var duplicate = new OpportunitySource { WorkspaceId = setup.Workspace, OpportunityId = setup.Opportunity,
                SourceLabel = "Duplicate", SourceConfigurationId = setup.Configuration, ExternalId = urlIdentity ? null : "one",
                SourceUrl = urlIdentity ? "HTTPS://EXAMPLE.INVALID/Job" : null,
                NormalizedSourceUrl = urlIdentity ? "https://example.invalid/Job" : null };
            db.OpportunitySources.Add(duplicate);
            Assert.Equal(PostgresErrorCodes.UniqueViolation,
                Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException).SqlState);
            db.ChangeTracker.Clear();
        }
        var textIndex = db.Model.FindEntityType(typeof(SourceExecutionItem))!.GetIndexes()
            .Single(x => x.Properties.Select(p => p.Name).SequenceEqual(new[] { "WorkspaceId", "NormalizedTitle", "NormalizedCompanyName" }));
        Assert.False(textIndex.IsUnique);
    }
}
