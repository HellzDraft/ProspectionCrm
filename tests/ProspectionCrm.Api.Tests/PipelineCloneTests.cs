using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Pipelines;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class PipelineCloneTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18").Build();
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private Guid workspaceId;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        workspaceId = (await new BootstrapService(db).BootstrapAsync(default)).Bootstrap!.WorkspaceId;
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Testing").UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString()));
        client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null)
            await factory.DisposeAsync();
        await postgres.DisposeAsync();
    }

    private ProspectionCrmDbContext CreateContext(SaveGate? gate = null)
    {
        var options = new DbContextOptionsBuilder<ProspectionCrmDbContext>().UseNpgsql(postgres.GetConnectionString());
        if (gate is not null)
            options.AddInterceptors(gate);
        return new(options.Options);
    }

    private static PipelineService Service(ProspectionCrmDbContext db) => new(db, new CurrentWorkspaceProvider(db));

    private async Task<Pipeline> SeedAsync(bool archived = false, bool visible = false)
    {
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        var source = new Pipeline
        {
            WorkspaceId = workspaceId, Name = "Source", TypeCode = "employment", Description = "Configuration",
            IsVisible = visible, CreatedAt = old, UpdatedAt = old, ArchivedAt = archived ? old : null,
            Stages = new[] { 0, 2, 4 }.Select((position, i) => new PipelineStage
            {
                Name = $"Stage {i}", Description = $"Description {i}", CategoryCode = new[] { "active", "success", "failure" }[i],
                SortOrder = position, CreatedAt = old, UpdatedAt = old
            }).ToList()
        };
        await using var db = CreateContext();
        db.Pipelines.Add(source);
        await db.SaveChangesAsync();
        return source;
    }

    private async Task<PipelineDto> CloneAsync(Guid id, object body)
    {
        using var response = await client.PostAsJsonAsync($"/api/pipelines/{id}/clone", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var clone = (await response.Content.ReadFromJsonAsync<PipelineDto>())!;
        Assert.EndsWith($"/api/pipelines/{clone.Id}", response.Headers.Location?.ToString());
        var fetched = await client.GetFromJsonAsync<PipelineDto>(response.Headers.Location);
        Assert.NotNull(fetched);
        Assert.Equal(clone.Id, fetched.Id);
        Assert.Equal(clone.Name, fetched.Name);
        Assert.Equal(clone.Stages.Select(x => (x.Id, x.Name, x.SortOrder)), fetched.Stages.Select(x => (x.Id, x.Name, x.SortOrder)));
        return clone;
    }

    // Compare every persisted field of every table, excluding only newly created configuration rows.
    private async Task<string> SnapshotAsync(Guid? sourceId = null)
    {
        await using var db = CreateContext();
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        var result = new SortedDictionary<string, string>();
        foreach (var table in db.Model.GetEntityTypes().Select(x => x.GetTableName()!).Distinct().Order())
        {
            var filter = sourceId.HasValue ? table switch
            {
                "Pipelines" => " WHERE t.\"Id\" = @source",
                "PipelineStages" => " WHERE t.\"PipelineId\" = @source",
                _ => ""
            } : "";
            // Identifiers come exclusively from EF metadata; values are parameters.
            var quoted = table.Replace("\"", "\"\"");
            await using var command = new NpgsqlCommand(
                $"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]'::jsonb)::text FROM \"{quoted}\" t{filter}", connection);
            if (sourceId.HasValue)
                command.Parameters.AddWithValue("source", sourceId.Value);
            result[table] = (string)(await command.ExecuteScalarAsync())!;
        }
        return JsonSerializer.Serialize(result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task CloneCopiesOnlyConfigurationAndActiveStages(bool archived, bool visible)
    {
        var source = await SeedAsync(archived, visible);
        await using var setup = CreateContext();
        var profile = new CandidateProfile { WorkspaceId = workspaceId, Name = "Preferred" };
        setup.CandidateProfiles.Add(profile);
        (await setup.Pipelines.SingleAsync()).PreferredCandidateProfileId = profile.Id;
        setup.PipelineStages.Add(new PipelineStage
        {
            PipelineId = source.Id, Name = "Archived", CategoryCode = "active", SortOrder = 1, ArchivedAt = DateTimeOffset.UtcNow
        });
        if (!archived)
            (await setup.Workspaces.SingleAsync()).DefaultPipelineId = source.Id;
        var opportunity = new Opportunity
        {
            WorkspaceId = workspaceId, PipelineStageId = source.Stages.First().Id, Title = "Existing", PriorityCode = "high",
            Notes = "Preserve", Score = 50, ArchivedAt = archived ? DateTimeOffset.UtcNow : null
        };
        setup.Opportunities.Add(opportunity);
        setup.CrmTasks.Add(new CrmTask { Opportunity = opportunity, Title = "Task" });
        setup.Applications.Add(new Application { Opportunity = opportunity, StatusCode = "draft" });
        setup.Proposals.Add(new Proposal { Opportunity = opportunity, StatusCode = "draft" });
        setup.EmailMessages.Add(new EmailMessage
        {
            WorkspaceId = workspaceId, Opportunity = opportunity, ProviderCode = "manual", DirectionCode = "inbound",
            FromAddress = "test@example.com", ToAddressesJson = "[]", OccurredAt = DateTimeOffset.UtcNow
        });
        setup.CalendarEvents.Add(new CalendarEvent
        {
            WorkspaceId = workspaceId, Opportunity = opportunity, ProviderCode = "manual", Title = "Meeting",
            StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        setup.ActivityEntries.Add(new ActivityEntry
        {
            WorkspaceId = workspaceId, RelatedOpportunity = opportunity, EntityTypeCode = "opportunity", EntityId = opportunity.Id,
            EventTypeCode = "created", ActorTypeCode = "user", OccurredAt = DateTimeOffset.UtcNow
        });
        setup.AutomationExecutions.Add(new AutomationExecution
        {
            WorkspaceId = workspaceId, StatusCode = "succeeded", TriggeredAt = DateTimeOffset.UtcNow,
            AutomationRule = new AutomationRule
            {
                WorkspaceId = workspaceId, PipelineId = source.Id, Name = "Rule", TriggerTypeCode = "manual", ActionTypeCode = "test"
            }
        });
        await setup.SaveChangesAsync();
        var before = await SnapshotAsync(source.Id);
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);
        var clone = await CloneAsync(source.Id, new
        {
            Name = "  Clone  ", Id = source.Id, WorkspaceId = Guid.NewGuid(), TypeCode = "custom", Description = "Rejected",
            IsVisible = !visible, PreferredCandidateProfileId = Guid.NewGuid(), ArchivedAt = DateTimeOffset.UtcNow,
            IsDefault = true, DefaultPipelineId = source.Id, Stages = new[] { new { Name = "Injected", SortOrder = 99 } }
        });
        Assert.NotEqual(source.Id, clone.Id);
        Assert.Equal("Clone", clone.Name);
        Assert.Equal(source.TypeCode, clone.TypeCode);
        Assert.Equal(source.Description, clone.Description);
        Assert.Equal(visible, clone.IsVisible);
        Assert.Equal(profile.Id, clone.PreferredCandidateProfileId);
        Assert.False(clone.IsDefault);
        Assert.Null(clone.ArchivedAt);
        Assert.Null(clone.UpdatedAt);
        Assert.InRange(clone.CreatedAt, start, DateTimeOffset.UtcNow);
        Assert.Equal(new[] { 0, 1, 2 }, clone.Stages.Select(x => x.SortOrder));
        var originals = source.Stages.OrderBy(x => x.SortOrder).ToArray();
        for (var i = 0; i < originals.Length; i++)
        {
            var copied = clone.Stages[i];
            Assert.DoesNotContain(copied.Id, originals.Select(x => x.Id));
            Assert.Equal(clone.Id, copied.PipelineId);
            Assert.Equal(originals[i].Name, copied.Name);
            Assert.Equal(originals[i].Description, copied.Description);
            Assert.Equal(originals[i].CategoryCode, copied.CategoryCode);
            Assert.Null(copied.ArchivedAt);
            Assert.Null(copied.UpdatedAt);
            Assert.InRange(copied.CreatedAt, start, DateTimeOffset.UtcNow);
        }
        Assert.Equal(3, clone.Stages.Select(x => x.Id).Distinct().Count());
        Assert.Equal(before, await SnapshotAsync(source.Id));
        await using var check = CreateContext();
        Assert.Equal(workspaceId, (await check.Pipelines.SingleAsync(x => x.Id == clone.Id)).WorkspaceId);
        Assert.Equal(2, await check.Pipelines.CountAsync());
        Assert.Equal(7, await check.PipelineStages.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("too-long")]
    public async Task InvalidNamesReturn400WithoutWriting(string? name)
    {
        var source = await SeedAsync();
        var before = await SnapshotAsync();
        using var response = await client.PostAsJsonAsync($"/api/pipelines/{source.Id}/clone",
            new { Name = name == "too-long" ? new string('n', 201) : name });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task MissingNameIsInvalidAndBoundaryAndDuplicateNamesAreAccepted()
    {
        var source = await SeedAsync();
        using var missing = await client.PostAsJsonAsync($"/api/pipelines/{source.Id}/clone", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var boundary = await CloneAsync(source.Id, new { Name = "  " + new string('n', 200) + "  " });
        Assert.Equal(new string('n', 200), boundary.Name);
        Assert.Null(boundary.PreferredCandidateProfileId);
        var same = await CloneAsync(source.Id, new { source.Name });
        var sameAgain = await CloneAsync(source.Id, new { source.Name });
        Assert.Equal(source.Name, same.Name);
        Assert.Equal(source.Name, sameAgain.Name);
        Assert.NotEqual(same.Id, sameAgain.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoActiveStagesProducesEmptyClone(bool archivedOnly)
    {
        var source = await SeedAsync();
        await using var db = CreateContext();
        if (archivedOnly)
            await db.PipelineStages.ExecuteUpdateAsync(x => x.SetProperty(s => s.ArchivedAt, DateTimeOffset.UtcNow));
        else
            await db.PipelineStages.ExecuteDeleteAsync();
        var before = await SnapshotAsync(source.Id);
        Assert.Empty((await CloneAsync(source.Id, new { Name = "Empty" })).Stages);
        Assert.Equal(before, await SnapshotAsync(source.Id));
    }

    [Fact]
    public async Task MissingOrForeignSourceReturns404()
    {
        await SeedAsync();
        await using var db = CreateContext();
        var other = new Workspace
        {
            OwnerUserId = (await db.UserAccounts.SingleAsync()).Id, Name = "Foreign", TimeZoneId = "UTC", ArchivedAt = DateTimeOffset.UtcNow
        };
        var source = new Pipeline { Workspace = other, Name = "Foreign", TypeCode = "custom" };
        db.Pipelines.Add(source);
        await db.SaveChangesAsync();
        var before = await SnapshotAsync();
        foreach (var id in new[] { source.Id, Guid.NewGuid() })
        {
            using var response = await client.PostAsJsonAsync($"/api/pipelines/{id}/clone", new { Name = "Rejected" });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferredProfileIsSharedOnlyWithinWorkspace(bool foreign)
    {
        var source = await SeedAsync();
        await using var db = CreateContext();
        var profile = new CandidateProfile { WorkspaceId = workspaceId, Name = "Preferred", ArchivedAt = DateTimeOffset.UtcNow };
        if (foreign)
            profile.Workspace = new Workspace
            {
                OwnerUserId = (await db.UserAccounts.SingleAsync()).Id, Name = "Foreign", TimeZoneId = "UTC", ArchivedAt = DateTimeOffset.UtcNow
            };
        db.CandidateProfiles.Add(profile);
        (await db.Pipelines.SingleAsync()).PreferredCandidateProfileId = profile.Id;
        await db.SaveChangesAsync(); // Proves the current FK permits a foreign-workspace profile.
        var before = await SnapshotAsync(foreign ? null : source.Id);
        if (foreign)
        {
            using var response = await client.PostAsJsonAsync($"/api/pipelines/{source.Id}/clone", new { Name = "Rejected" });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        else
            Assert.Equal(profile.Id, (await CloneAsync(source.Id, new { Name = "Shared" })).PreferredCandidateProfileId);
        Assert.Equal(before, await SnapshotAsync(foreign ? null : source.Id));
    }

    [Fact]
    public async Task FailedStageInsertRollsBackEntireClone()
    {
        var source = await SeedAsync();
        var before = await SnapshotAsync();
        await using var db = CreateContext();
        // Original slots 0/2/4 satisfy this; the clone's normalized second slot is rejected.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"PipelineStages\" ADD CONSTRAINT reject_clone_slot CHECK (\"SortOrder\" <> 1)");
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Service(db).CloneAsync(source.Id, new() { Name = "Fails" }, default));
        Assert.Equal("reject_clone_slot", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("archive")]
    [InlineData("restore")]
    [InlineData("reorder")]
    public async Task CloneWaitsForStageWriterAndCopiesCommittedConfiguration(string operation)
    {
        var source = await SeedAsync();
        var target = source.Stages.First().Id;
        if (operation == "restore")
        {
            await using var setup = CreateContext();
            (await setup.PipelineStages.SingleAsync(x => x.Id == target)).ArchivedAt = DateTimeOffset.UtcNow;
            await setup.SaveChangesAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var gate = new SaveGate(afterSave: true); // For reorder this pauses after the temporary positions have been persisted.
        await using var writer = CreateContext(gate);
        var stages = new PipelineStageService(writer, new CurrentWorkspaceProvider(writer));
        var write = operation switch
        {
            "create" => stages.CreateAsync(source.Id, new() { Name = "New", CategoryCode = "active" }, token),
            "update" => stages.UpdateAsync(source.Id, target, new() { Name = "Changed", CategoryCode = "failure" }, token),
            "archive" => stages.ArchiveAsync(source.Id, target, token),
            "restore" => stages.RestoreAsync(source.Id, target, token),
            _ => stages.ReorderAsync(source.Id, new() { StageIds = source.Stages.Reverse().Select(x => x.Id).ToArray() }, token)
        };
        await gate.Reached.Task.WaitAsync(token);
        await using var cloner = CreateContext();
        var cloneTask = Service(cloner).CloneAsync(source.Id, new() { Name = "Waited" }, token);
        await WaitForBlockedAsync(1, token);
        Assert.False(cloneTask.IsCompleted);
        gate.Release.TrySetResult();
        Assert.Equal(PipelineStageWriteStatus.Succeeded, (await write).Status);
        var result = await cloneTask;
        Assert.Equal(PipelineCloneStatus.Succeeded, result.Status);
        await using var check = CreateContext();
        var committed = await check.PipelineStages.AsNoTracking().Where(x => x.PipelineId == source.Id && x.ArchivedAt == null)
            .OrderBy(x => x.SortOrder).ToArrayAsync(token);
        Assert.Equal(committed.Select(x => (x.Name, x.Description, x.CategoryCode)),
            result.Pipeline!.Stages.Select(x => (x.Name, x.Description, x.CategoryCode)));
        Assert.Equal(Enumerable.Range(0, committed.Length), result.Pipeline.Stages.Select(x => x.SortOrder));
    }

    [Fact]
    public async Task TwoConcurrentClonesSucceedIndependentlyAndOtherPipelineIsNotBlocked()
    {
        var source = await SeedAsync();
        var other = await SeedAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var blocker = CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(token);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Pipelines\" WHERE \"Id\" = {source.Id} FOR UPDATE", token);
        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var first = Service(firstDb).CloneAsync(source.Id, new() { Name = "Same" }, token);
        var second = Service(secondDb).CloneAsync(source.Id, new() { Name = "Same" }, token);
        await WaitForBlockedAsync(2, token);
        await using var otherDb = CreateContext();
        Assert.Equal(PipelineCloneStatus.Succeeded, (await Service(otherDb).CloneAsync(other.Id, new() { Name = "Other" }, token)).Status);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await transaction.CommitAsync(token);
        var results = await Task.WhenAll(first, second);
        Assert.All(results, x => Assert.Equal(PipelineCloneStatus.Succeeded, x.Status));
        Assert.NotEqual(results[0].Pipeline!.Id, results[1].Pipeline!.Id);
        Assert.Empty(results[0].Pipeline!.Stages.Select(x => x.Id).Intersect(results[1].Pipeline!.Stages.Select(x => x.Id)));
        Assert.Equal(5, await otherDb.Pipelines.CountAsync(token));
        Assert.All(results, x => Assert.Equal(new[] { 0, 1, 2 }, x.Pipeline!.Stages.Select(s => s.SortOrder)));
    }

    [Fact]
    public async Task CloneAndArchiveDefaultSourceCannotDeadlockOnWorkspaceForeignKey()
    {
        var source = await SeedAsync();
        await using (var setup = CreateContext())
        {
            (await setup.Workspaces.SingleAsync()).DefaultPipelineId = source.Id;
            await setup.SaveChangesAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var gate = new SaveGate(afterSave: false);
        await using var cloner = CreateContext(gate);
        var cloneTask = Service(cloner).CloneAsync(source.Id, new() { Name = "Independent" }, token);
        await gate.Reached.Task.WaitAsync(token); // Source is locked, INSERT/FK check has not happened yet.
        await using var archiver = CreateContext();
        var archiveTask = Service(archiver).ArchiveAsync(source.Id, token);
        await WaitForBlockedAsync(1, token); // Archive owns the workspace lock and waits for the source row.
        gate.Release.TrySetResult();
        var clone = await cloneTask;
        Assert.Equal(PipelineCloneStatus.Succeeded, clone.Status);
        Assert.True(await archiveTask);
        await using var check = CreateContext();
        Assert.NotNull((await check.Pipelines.SingleAsync(x => x.Id == source.Id, token)).ArchivedAt);
        Assert.Null((await check.Pipelines.SingleAsync(x => x.Id == clone.Pipeline!.Id, token)).ArchivedAt);
        Assert.Null((await check.Workspaces.SingleAsync(token)).DefaultPipelineId);
    }

    private async Task WaitForBlockedAsync(int count, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(postgres.GetConnectionString());
        await observer.OpenAsync(token);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%Pipelines%'", observer);
        while (Convert.ToInt64(await command.ExecuteScalarAsync(token)) < count)
            await Task.Delay(25, token);
    }

    private sealed class SaveGate(bool afterSave) : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool paused;

        private async Task PauseAsync(CancellationToken token)
        {
            if (paused) return;
            paused = true;
            Reached.TrySetResult();
            await Release.Task.WaitAsync(token);
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!afterSave) await PauseAsync(cancellationToken);
            return result;
        }

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (afterSave) await PauseAsync(cancellationToken);
            return result;
        }
    }
}
