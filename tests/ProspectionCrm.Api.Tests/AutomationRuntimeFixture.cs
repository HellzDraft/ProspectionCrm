using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public abstract class AutomationRuntimeFixture(AutomationJobDatabase database) : AutomationEvaluationFixture(database)
{
    protected sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(2030, 1, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    protected readonly TestClock Clock = new();
    protected readonly AutomationWorkerOptions Config = new();
    protected sealed record Setup(Guid Workspace, AutomationRule Rule, Opportunity Opportunity, AutomationJob Job);

    protected async Task<Setup> Prepare(string mode = "automatic", bool enabled = true, int? days = 3, Guid? workspace = null)
    {
        var ws = workspace ?? await Workspace();
        await Settings(ws, s => { s.IsEnabled = enabled; s.OperatingModeCode = mode; });
        var pipeline = await Pipeline(ws);
        var rule = await ValidRule(ws, change: r => r.ActionConfigurationJson = days is { } n
            ? JsonSerializer.Serialize(new { title = "Relancer", description = "Texte fixe", dueInDays = n })
            : """{"title":"Relancer","description":"Texte fixe"}""");
        await using var db = Db();
        var opportunity = new Opportunity { WorkspaceId = ws, Title = "Opportunity", PriorityCode = "normal",
            PipelineStage = new() { PipelineId = pipeline, Name = "Open", CategoryCode = "active" } };
        db.Add(opportunity); await db.SaveChangesAsync();
        var job = await Add(ws, Request(rule: rule.Id, context: Context(opportunity.Id)));
        return new(ws, rule, opportunity, job);
    }
    protected static string Context(Guid opportunity) => JsonSerializer.Serialize(new {
        eventKey = "runtime-test", pipelineId = (Guid?)null, payload = new { opportunityId = opportunity, source = "manual" }
    });
    protected async Task<AutomationJob> Another(Setup setup) => await Add(setup.Workspace,
        Request(rule: setup.Rule.Id, context: Context(setup.Opportunity.Id)));
    protected async Task Settings(Guid ws, Action<AutomationRuntimeSettings> change)
    {
        await using var db = Db(); var settings = await db.AutomationRuntimeSettings.SingleAsync(x => x.WorkspaceId == ws);
        change(settings); await db.SaveChangesAsync();
    }
    protected async Task ChangeRule(Guid id, Action<AutomationRule> change)
    {
        await using var db = Db(); var rule = await db.AutomationRules.SingleAsync(x => x.Id == id);
        change(rule); await db.SaveChangesAsync();
    }
    protected AutomationRuntimeStore Runtime(ProspectionCrmDbContext db, IAutomationRuleEvaluator? evaluator = null) =>
        new(db, evaluator ?? new AutomationRuleEvaluator(new AutomationSafetyPolicy()), Clock, Options.Create(Config));
    protected async Task<AutomationJob> Claim(Guid ws)
    {
        await using var db = Db(); return Assert.IsType<AutomationJob>(await Queue(db).ClaimAsync(ws, default));
    }
    protected async Task Process(AutomationJob claim,
        Func<ProspectionCrmDbContext, AutomationRuntimeStore, IAutomationActionExecutor>? executor = null,
        Func<IAutomationJobQueue, IAutomationJobQueue>? queue = null, CancellationToken token = default,
        IAutomationRuleEvaluator? evaluator = null, string? owner = null, IInterceptor? interceptor = null)
    {
        await using var db = interceptor is null ? Db() : InterceptedDb(interceptor);
        var runtime = Runtime(db, evaluator); var realQueue = Queue(db);
        var processor = new AutomationJobProcessor(db, queue?.Invoke(realQueue) ?? realQueue, runtime,
            executor?.Invoke(db, runtime) ?? new CreateCrmTaskAutomationExecutor(db, runtime),
            Options.Create(Config), NullLogger<AutomationJobProcessor>.Instance, new AutomationActionRequestStore(db, runtime));
        await processor.ProcessAsync(claim, owner ?? claim.LeaseOwner!, token);
    }
    protected ProspectionCrmDbContext InterceptedDb(IInterceptor interceptor)
    {
        using var configuration = Db();
        return new(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
            .UseNpgsql(configuration.Database.GetConnectionString()).AddInterceptors(interceptor).Options);
    }
    protected async Task Run(Setup setup,
        Func<ProspectionCrmDbContext, AutomationRuntimeStore, IAutomationActionExecutor>? executor = null)
        => await Process(await Claim(setup.Workspace), executor);
    protected async Task<AutomationJob> Job(Guid id)
    { await using var db = Db(); return await db.AutomationJobs.AsNoTracking().SingleAsync(x => x.Id == id); }
    protected async Task<List<AutomationExecution>> Executions(Guid job)
    { await using var db = Db(); return await db.AutomationExecutions.AsNoTracking().Where(x => x.AutomationJobId == job).OrderBy(x => x.AttemptNumber).ToListAsync(); }
    protected async Task<List<CrmTask>> Tasks(Guid job)
    { await using var db = Db(); return await db.CrmTasks.AsNoTracking().Where(x => x.AutomationJobId == job).ToListAsync(); }
    protected async Task Available(Guid job)
    {
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AutomationJobs\" SET \"AvailableAt\" = clock_timestamp() WHERE \"Id\" = {job}");
    }
    protected async Task Expire(Guid job)
    {
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AutomationJobs\" SET \"LeaseExpiresAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = {job}");
    }
    protected async Task<int> Recover(Guid ws)
    {
        await using var db = Db(); return await new AutomationJobRecovery(db, Queue(db), Runtime(db)).RecoverAsync(ws, default);
    }
    protected sealed class DelegateExecutor(Func<AutomationJob, CreateCrmTaskPlan, DateTimeOffset, CancellationToken, Task<AutomationActionResult>> execute)
        : IAutomationActionExecutor
    {
        public Task<AutomationActionResult> ExecuteAsync(AutomationJob job, CreateCrmTaskPlan plan, DateTimeOffset now, CancellationToken token)
            => execute(job, plan, now, token);
    }
}
