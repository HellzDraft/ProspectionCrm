using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationSupervision;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public abstract class AutomationSupervisionFixture(AutomationJobDatabase database) : AutomationActionRequestFixture(database)
{
    protected readonly AutomationSupervisionOptions SupervisionOptions = new();
    protected sealed class CurrentWorkspace(Guid id) : ICurrentWorkspaceProvider
    {
        public Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(id);
    }
    protected async Task<AutomationCircuitStatus> Circuit(Guid workspace)
    {
        await using var db = Db();
        return await new AutomationCircuitBreakerService(db, Clock).ReadAsync(
            await db.AutomationRuntimeSettings.AsNoTracking().SingleAsync(x => x.WorkspaceId == workspace), default);
    }
    protected async Task<AutomationCircuitResetResult> Reset(Guid workspace, string? note = null)
    {
        await using var db = Db();
        var result = await new AutomationCircuitBreakerService(db, Clock).ResetAsync(workspace, new() { Note = note }, default);
        Assert.Equal(200, result.Status); return Assert.IsType<AutomationCircuitResetResult>(result.Value);
    }
    protected async Task<AutomationSupervisionDto> Supervise(Guid workspace)
    {
        await using var db = Db();
        var circuit = new AutomationCircuitBreakerService(db, Clock);
        var guard = new AutomationRuntimeGuard(db, circuit, Clock, Options.Create(Config));
        var service = new AutomationSupervisionService(db, new(db, new CurrentWorkspace(workspace)),
            guard, Clock, Options.Create(Config), Options.Create(SupervisionOptions));
        var result = await service.ReadAsync(default);
        Assert.Equal(200, result.Status); return Assert.IsType<AutomationSupervisionDto>(result.Value);
    }
    protected WebApplicationFactory<Program> SupervisionFactory(Guid? workspace) => Factory(workspace)
        .WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(Clock);
        }));

    protected async Task<AutomationExecution> Outcome(Setup setup, long sequence, bool success, DateTimeOffset? at = null)
    {
        var time = at ?? Clock.Now; var job = await Another(setup);
        await using var db = Db();
        await db.AutomationJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.StatusCode, success ? "completed" : "failed")
            .SetProperty(x => x.AttemptCount, 1).SetProperty(x => x.CompletedAt, time));
        var e = new AutomationExecution { WorkspaceId = setup.Workspace, AutomationRuleId = setup.Rule.Id,
            AutomationJobId = job.Id, AttemptNumber = 1, StatusCode = success ? "succeeded" : "failed",
            IsAutomaticAttempt = true, OutcomeSequence = sequence, EffectApplied = success,
            TriggeredAt = time, StartedAt = time, FinishedAt = time, ReasonCode = success ? "task-created" : "technical-failure" };
        db.Add(e); await db.SaveChangesAsync(); return e;
    }
    protected static IAutomationActionExecutor TechnicalFailure() => new DelegateExecutor((_, _, _, _) =>
        throw new InvalidOperationException("secret-marker"));
    protected async Task<string[]> BusinessSnapshot(Guid workspace)
    {
        await using var db = Db();
        return await db.Database.SqlQuery<string>($"""
            SELECT 'job:' || to_jsonb(j)::text AS "Value" FROM "AutomationJobs" j WHERE "WorkspaceId" = {workspace}
            UNION ALL SELECT 'execution:' || to_jsonb(e)::text FROM "AutomationExecutions" e WHERE "WorkspaceId" = {workspace}
            UNION ALL SELECT 'request:' || to_jsonb(r)::text FROM "AutomationActionRequests" r WHERE "WorkspaceId" = {workspace}
            UNION ALL SELECT 'settings:' || to_jsonb(s)::text FROM "AutomationRuntimeSettings" s WHERE "WorkspaceId" = {workspace}
            UNION ALL SELECT 'task:' || to_jsonb(t)::text FROM "CrmTasks" t JOIN "Opportunities" o ON o."Id" = t."OpportunityId"
                WHERE o."WorkspaceId" = {workspace}
            ORDER BY "Value"
            """).ToArrayAsync();
    }
}
