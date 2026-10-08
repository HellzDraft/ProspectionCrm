using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationEventDispatcherTests(AutomationJobDatabase database) : AutomationEvaluationFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task SelectsOnlyWorkspaceTriggerActiveAndPipelineCandidatesWithoutEvaluatingDefinitions()
    {
        var workspace = await Workspace(); var foreign = await Workspace(); var pipeline = await Pipeline(workspace);
        var otherPipeline = await Pipeline(workspace);
        var global = await ValidRule(workspace, change: r => r.ConditionJson = """{"type":"context-equals","property":"source","value":"other"}""");
        var scoped = await ValidRule(workspace, pipeline, r => { r.ConditionJson = """{"type":"legacy"}"""; r.ActionConfigurationJson = "{}"; });
        await ValidRule(workspace, otherPipeline);
        await ValidRule(workspace, change: r => r.Enabled = false);
        await ValidRule(workspace, change: r => r.ArchivedAt = DateTimeOffset.UtcNow);
        await ValidRule(workspace, change: r => r.TriggerTypeCode = "legacy");
        var foreignRule = await ValidRule(foreign);
        await using var db = Db();
        Assert.False((await db.AutomationRuntimeSettings.SingleAsync(x => x.WorkspaceId == workspace)).IsEnabled);
        var result = await Dispatcher(db).DispatchAsync(new(workspace, "manual", "same-event", pipeline, Payload), default);
        Assert.Equal(200, result.Status); var summary = result.Value!;
        Assert.Equal(2, summary.CandidateRuleCount); Assert.Equal(2, summary.CreatedCount); Assert.Equal(0, summary.ExistingCount);
        Assert.Equal(new[] { global.Id, scoped.Id }.Order(), summary.Jobs.Select(x => x.AutomationRuleId).Order());
        var jobs = await db.AutomationJobs.AsNoTracking().Where(x => x.WorkspaceId == workspace).ToListAsync();
        Assert.All(jobs, job =>
        {
            Assert.Equal("general", job.ActionCategoryCode); Assert.Equal(50, job.Priority); Assert.Equal("pending", job.StatusCode);
            Assert.Equal(job.CreatedAt, job.AvailableAt); Assert.Equal(0, job.AttemptCount); Assert.Null(job.UpdatedAt);
            Assert.Null(job.LeaseOwner); Assert.Null(job.LeaseExpiresAt); Assert.NotNull(job.AutomationRuleId);
            Assert.True(AutomationEventContext.TryParse(job.ContextJson, out var context)); Assert.Equal(pipeline, context.PipelineId);
            Assert.Equal("same-event", context.EventKey);
        });
        var noPipeline = (await Dispatcher(db).DispatchAsync(new(workspace, "manual", "no-pipeline", PayloadJson: Payload), default)).Value!;
        Assert.Equal(global.Id, Assert.Single(noPipeline.Jobs).AutomationRuleId);
        var otherWorkspace = (await Dispatcher(db).DispatchAsync(new(foreign, "manual", "same-event", PayloadJson: Payload), default)).Value!;
        Assert.Equal(foreignRule.Id, Assert.Single(otherWorkspace.Jobs).AutomationRuleId);
        Assert.Empty(await db.AutomationExecutions.ToListAsync()); Assert.Empty(await db.CrmTasks.ToListAsync());
        Assert.Empty(await db.Opportunities.ToListAsync()); Assert.Empty(await db.ActivityEntries.ToListAsync());
    }

    [Fact]
    public async Task RepeatedEventsReturnFirstDurableContextEvenAfterTerminalStatusAndAllowMixedResults()
    {
        var workspace = await Workspace(); await ValidRule(workspace); await using var db = Db();
        var first = (await Dispatcher(db).DispatchAsync(new(workspace, "manual", "event", PayloadJson: Payload), default)).Value!;
        var id = Assert.Single(first.Jobs).JobId; await Queue(db).CancelAsync(workspace, id, default);
        var extra = await ValidRule(workspace);
        var repeat = (await Dispatcher(db).DispatchAsync(new(workspace, "manual", "event", PayloadJson: "{\"source\":\"changed\"}"), default)).Value!;
        Assert.Equal(2, repeat.CandidateRuleCount); Assert.Equal(1, repeat.CreatedCount); Assert.Equal(1, repeat.ExistingCount);
        Assert.Equal(id, repeat.Jobs.Single(x => !x.IsCreated).JobId); Assert.Equal(extra.Id, repeat.Jobs.Single(x => x.IsCreated).AutomationRuleId);
        var stored = await db.AutomationJobs.SingleAsync(x => x.Id == id); Assert.Equal("cancelled", stored.StatusCode);
        Assert.True(AutomationEventContext.TryParse(stored.ContextJson, out var context)); Assert.Equal("manual", context.Payload.GetProperty("source").GetString());
        // Settings are irrelevant to persistence, including a historical workspace with no settings row.
        await db.AutomationRuntimeSettings.Where(x => x.WorkspaceId == workspace).ExecuteDeleteAsync();
        Assert.Equal(2, (await Dispatcher(db).DispatchAsync(new(workspace, "manual", "next"), default)).Value!.CreatedCount);
    }

    [Fact]
    public async Task ConcurrentDispatchesProduceExactlyOneJobPerRule()
    {
        var workspace = await Workspace(); await ValidRule(workspace); await ValidRule(workspace); await ValidRule(workspace);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var arrivals = 0;
        async Task<AutomationJobResult<AutomationDispatchSummary>> Dispatch()
        {
            await using var db = Db(); if (Interlocked.Increment(ref arrivals) == 6) gate.SetResult();
            await gate.Task; return await Dispatcher(db).DispatchAsync(new(workspace, "manual", "race", PayloadJson: Payload), default);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Dispatch())).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(results, x => { Assert.Equal(200, x.Status); Assert.Equal(3, x.Value!.CandidateRuleCount); });
        Assert.Equal(3, results.Sum(x => x.Value!.CreatedCount)); Assert.Equal(15, results.Sum(x => x.Value!.ExistingCount));
        Assert.Equal(3, results.SelectMany(x => x.Value!.Jobs).Select(x => x.JobId).Distinct().Count());
        await using var verify = Db(); Assert.Equal(3, await verify.AutomationJobs.CountAsync(x => x.WorkspaceId == workspace));
    }

    [Fact]
    public async Task UnsupportedHistoricalActionFailsBeforeAnyEnqueue()
    {
        var workspace = await Workspace(); await ValidRule(workspace); await Rule(workspace); await using var db = Db();
        var result = await Dispatcher(db).DispatchAsync(new(workspace, "manual", "legacy"), default);
        Assert.Equal(409, result.Status); Assert.Equal("unsupported-action", result.Code);
        Assert.Empty(await db.AutomationJobs.Where(x => x.WorkspaceId == workspace).ToListAsync());
    }

    [Fact]
    public async Task AdministrativeKeyCollisionRollsBackPreviouslyInsertedCandidates()
    {
        var workspace = await Workspace();
        await ValidRule(workspace, change: r => r.Id = Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var last = await ValidRule(workspace, change: r => r.Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        var existing = await Add(workspace, Request(AutomationEventContext.TriggerKey("event", last.Id)));
        await using var db = Db(); var result = await Dispatcher(db).DispatchAsync(new(workspace, "manual", "event"), default);
        Assert.Equal(409, result.Status); Assert.Equal("dispatch-key-conflict", result.Code);
        Assert.Equal(existing.Id, Assert.Single(await db.AutomationJobs.Where(x => x.WorkspaceId == workspace).ToListAsync()).Id);
    }

    [Fact]
    public async Task SchemaAndTaskLimitsRemainUnchangedAndJsonSurvivesPostgreSqlNormalization()
    {
        var workspace = await Workspace(); var rule = await ValidRule(workspace, change: r =>
            r.ConditionJson = """{"type":"context-equals","property":"number","value":1.0}""");
        await using var db = Db();
        Assert.Equal("20261008083956_Phase82AutomationJobs", db.Database.GetMigrations().Last());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        var task = db.Model.FindEntityType(typeof(CrmTask))!;
        Assert.Equal(AutomationDefinitionLimits.TaskTitleLength, task.FindProperty(nameof(CrmTask.Title))!.GetMaxLength());
        Assert.Equal(AutomationDefinitionLimits.TaskDescriptionLength, task.FindProperty(nameof(CrmTask.Description))!.GetMaxLength());
        const string payload = """{"number":1e0,"opportunityId":"00000000-0000-0000-0000-000000000001"}""";
        var result = await Dispatcher(db).DispatchAsync(new(workspace, "manual", "number", PayloadJson: payload), default);
        var job = await db.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == result.Value!.Jobs.Single().JobId);
        var settings = await db.AutomationRuntimeSettings.SingleAsync(x => x.WorkspaceId == workspace);
        settings.IsEnabled = true;
        Assert.True(new AutomationRuleEvaluator(new AutomationSafetyPolicy()).Evaluate(job, rule, settings).IsMatched);
    }
}
