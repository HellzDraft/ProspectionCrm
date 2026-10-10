using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Dtos.AutomationJobs;
using ProspectionCrm.Api.Dtos.AutomationSupervision;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationPhase8EndToEndTests(AutomationJobDatabase database)
    : AutomationSupervisionFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task HostedWorkerConsumesAnHttpEventAndSupervisionObservesTheCommittedEffect()
    {
        Clock.Now = DateTimeOffset.UtcNow;
        var setup = await Prepare();
        await using (var db = Db()) await db.AutomationJobs.Where(x => x.Id == setup.Job.Id).ExecuteDeleteAsync();
        using var factory = SupervisionFactory(setup.Workspace).WithWebHostBuilder(b => b
            .UseSetting("AutomationWorker:Enabled", "true").UseSetting("AutomationWorker:IdleDelaySeconds", "1"));
        using var client = Client(factory);
        var response = await client.PostAsJsonAsync("/api/automation-events", new {
            triggerTypeCode = "manual", eventKey = "hosted-phase86", payload = new { opportunityId = setup.Opportunity.Id } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var job = Assert.Single((await response.Content.ReadFromJsonAsync<AutomationDispatchSummary>())!.Jobs).JobId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((await Job(job)).StatusCode != "completed") await Task.Delay(20, timeout.Token);
        Clock.Now = DateTimeOffset.UtcNow;
        var snapshot = (await client.GetFromJsonAsync<AutomationSupervisionDto>("/api/automation-supervision"))!;
        Assert.True(snapshot.Worker.ConfiguredEnabled);
        Assert.Equal(1, snapshot.Queue.CompletedLast24HoursCount);
        Assert.Equal(1, snapshot.Executions.AutomaticEffectsLast24HoursCount);
        Assert.Equal(1, snapshot.Quotas.Minute.Used);
        Assert.Single(await Tasks(job)); Assert.Single(await Executions(job));
    }

    [Theory]
    [InlineData("automatic")][InlineData("manual")][InlineData("assist")]
    public async Task EventToDispatchWorkerDecisionEffectAndSupervision(string mode)
    {
        Clock.Now = DateTimeOffset.UtcNow;
        var setup = await Prepare(mode);
        await using (var db = Db()) await db.AutomationJobs.Where(x => x.Id == setup.Job.Id).ExecuteDeleteAsync();
        using var factory = SupervisionFactory(setup.Workspace); using var client = Client(factory);
        var body = new { triggerTypeCode = "manual", eventKey = "phase8-end-to-end", payload = new { opportunityId = setup.Opportunity.Id } };
        var response = await client.PostAsJsonAsync("/api/automation-events", body);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var dispatched = (await response.Content.ReadFromJsonAsync<AutomationDispatchSummary>())!;
        var jobId = Assert.Single(dispatched.Jobs).JobId; setup = setup with { Job = await Job(jobId) };
        var replay = await client.PostAsJsonAsync("/api/automation-events", body);
        Assert.Equal(1,(await replay.Content.ReadFromJsonAsync<AutomationDispatchSummary>())!.ExistingCount);
        await Run(setup);
        if (mode != "automatic")
        {
            var waiting = await Supervise(setup.Workspace);
            Assert.Equal(1,waiting.Queue.AwaitingApprovalCount); Assert.Equal(1,waiting.ActionRequests.PendingCount);
            Assert.Empty(await Tasks(jobId)); Assert.Empty(await Executions(jobId));
            var request = await RequestFor(jobId);
            Assert.Equal(mode == "manual" ? "manual" : "approval-required",request.DecisionRequirementCode);
            var decision = await client.PostAsJsonAsync("/api/automation-action-requests/" + request.Id
                + (mode == "manual" ? "/approve" : "/reject"),new { decisionNote = "Décision explicite" });
            Assert.Equal(HttpStatusCode.OK,decision.StatusCode);
            Assert.Empty(await Tasks(jobId)); Assert.Empty(await Executions(jobId));
            if (mode == "manual") { Assert.Equal("pending",(await Job(jobId)).StatusCode); await Run(setup); }
        }
        Clock.Now = DateTimeOffset.UtcNow;
        var final = (await client.GetFromJsonAsync<AutomationSupervisionDto>("/api/automation-supervision"))!;
        Assert.Equal(mode == "assist" ? 0 : 1, final.Queue.CompletedLast24HoursCount);
        Assert.Equal(0,final.Queue.AwaitingApprovalCount); Assert.Equal(0,final.ActionRequests.PendingCount);
        Assert.Equal(mode == "assist" ? "cancelled" : "completed",(await Job(jobId)).StatusCode);
        if (mode == "assist")
        {
            Assert.Empty(await Tasks(jobId)); Assert.Empty(await Executions(jobId)); Assert.Equal(1,final.ActionRequests.RejectedLast24HoursCount);
        }
        else
        {
            Assert.Single(await Tasks(jobId)); var execution = Assert.Single(await Executions(jobId));
            Assert.Equal(mode == "automatic",execution.IsAutomaticAttempt); Assert.Equal(mode == "manual",execution.IsHumanApprovedAttempt);
            Assert.Equal(mode == "automatic" ? 1 : 0,final.Executions.AutomaticEffectsLast24HoursCount);
            Assert.Equal(mode == "manual" ? 1 : 0,final.Executions.HumanEffectsLast24HoursCount);
            Assert.Equal(mode == "automatic" ? 1 : 0,final.Quotas.Minute.Used);
        }
    }

    [Fact]
    public async Task OpenCircuitDefersWithoutHistoryResetPreservesAvailabilityAndNewFailuresReopen()
    {
        var setup = await Prepare(); await Settings(setup.Workspace,s => s.MaxConsecutiveFailures = 1);
        await Run(setup,(_,_) => TechnicalFailure());
        var next = setup with { Job = await Another(setup) }; await Run(next);
        var delayed = await Job(next.Job.Id); Assert.Equal(Clock.Now.AddSeconds(Config.DeferredDelaySeconds),delayed.AvailableAt);
        var open = await Supervise(setup.Workspace); Assert.Equal("open",open.Circuit.StatusCode);
        using var factory = SupervisionFactory(setup.Workspace); using var client = Client(factory);
        Assert.Equal(open.Circuit, await client.GetFromJsonAsync<AutomationCircuitStatus>("/api/automation-circuit-breaker"));
        for (var i = 0; i < 3; i++) { await Available(next.Job.Id); await Run(next); }
        Assert.Equal(delayed.AvailableAt, (await Job(next.Job.Id)).AvailableAt);
        Assert.Contains(open.Alerts,x => x.Code == AutomationAlertCodes.CircuitOpen && x.SeverityCode == "critical");
        await using (var db = Db()) Assert.Null(await Queue(db).ClaimAsync(setup.Workspace,default));
        Assert.Empty(await Executions(next.Job.Id)); Assert.Empty(await Tasks(next.Job.Id));
        var reset = await Reset(setup.Workspace); Assert.Equal("closed",reset.Status.StatusCode);
        Assert.Equal(delayed.AvailableAt,(await Job(next.Job.Id)).AvailableAt);
        await using (var db = Db()) Assert.Null(await Queue(db).ClaimAsync(setup.Workspace,default));
        // PostgreSQL owns queue availability; emulate arrival of that instant without waiting a minute.
        Clock.Now = delayed.AvailableAt; await Available(next.Job.Id); await Run(next);
        Assert.Single(await Tasks(next.Job.Id)); Assert.Single(await Executions(next.Job.Id));
        Assert.Equal("failed",(await Job(setup.Job.Id)).StatusCode);
        var failure = setup with { Job = await Another(setup) }; await Run(failure,(_,_) => TechnicalFailure());
        Assert.Equal("open",(await Circuit(setup.Workspace)).StatusCode);
        Assert.Equal(reset.Reset!.Id,(await Circuit(setup.Workspace)).LastResetId);
    }

    [Fact]
    public async Task HumanEffectBypassesOpenCircuitButKillSwitchBlocksBothOriginsAndResetDoesNotClearQuota()
    {
        var setup = await Approved();
        await Settings(setup.Workspace,s => { s.OperatingModeCode = "automatic"; s.MaxConsecutiveFailures = 1; s.MaxExecutionsPerMinute = 1; s.MaxExecutionsPerDay = 1; });
        // Persist completed automatic outcomes to saturate both independent guards.
        await Outcome(setup,1,true); await Outcome(setup,2,false);
        await Settings(setup.Workspace,s => s.IsEnabled = false); await Run(setup);
        Assert.Empty(await Tasks(setup.Job.Id)); Assert.Empty(await Executions(setup.Job.Id));
        await Settings(setup.Workspace,s => s.IsEnabled = true); await Available(setup.Job.Id); await Run(setup);
        Assert.Single(await Tasks(setup.Job.Id)); Assert.Equal("open",(await Circuit(setup.Workspace)).StatusCode);
        Assert.Equal(1,(await Supervise(setup.Workspace)).Quotas.Day.Used);
        await Reset(setup.Workspace);
        var next = setup with { Job = await Another(setup) }; await Run(next);
        Assert.Empty(await Executions(next.Job.Id)); Assert.Empty(await Tasks(next.Job.Id));
        Assert.Equal((await Supervise(setup.Workspace)).Quotas.Day.AvailableAgainAt,(await Job(next.Job.Id)).AvailableAt);
        await Settings(setup.Workspace,s => s.IsEnabled = false); await Available(next.Job.Id); await Run(next);
        Assert.Equal(Clock.Now.AddSeconds(Config.DeferredDelaySeconds),(await Job(next.Job.Id)).AvailableAt);
        Assert.Empty(await Executions(next.Job.Id));
    }

    [Theory]
    [InlineData("automatic")][InlineData("manual")]
    public async Task CommittedEffectThenLostCompletionRecoversWithoutDuplicationAndWithCoherentSupervision(string mode)
    {
        var setup = mode == "manual" ? await Approved() : await Prepare();
        var claim = await Claim(setup.Workspace); await Process(claim,queue:q => new BrokenCompletion(q));
        Assert.Equal("leased",(await Job(claim.Id)).StatusCode); Assert.Single(await Tasks(claim.Id));
        await Expire(claim.Id);
        Assert.Equal(1,(await Supervise(setup.Workspace)).Queue.LeasedExpiredCount);
        await Recover(setup.Workspace); await Run(setup);
        Assert.Single(await Tasks(claim.Id)); Assert.Single(await Executions(claim.Id));
        var snapshot = await Supervise(setup.Workspace);
        Assert.Equal(0,snapshot.Queue.LeasedExpiredCount); Assert.Equal(0,snapshot.Executions.RunningCount);
        Assert.Equal(0,snapshot.Executions.AbandonedRunningCount); Assert.Equal(1,snapshot.Executions.SucceededLast24HoursCount);
        Assert.Equal(mode == "automatic" ? 1 : 0,snapshot.Quotas.Minute.Used);
        Assert.Equal("completed",(await Job(claim.Id)).StatusCode);
    }

    private sealed class BrokenCompletion(IAutomationJobQueue inner) : IAutomationJobQueue
    {
        public Task<bool> CompleteAsync(Guid w,Guid j,string o,CancellationToken t) => throw new NpgsqlException("secret-marker");
        public Task<AutomationJobResult<AutomationJob>> EnqueueAsync(Guid w,EnqueueAutomationJobRequest r,CancellationToken t) => inner.EnqueueAsync(w,r,t);
        public Task<AutomationJob?> ClaimAsync(Guid w,CancellationToken t) => inner.ClaimAsync(w,t);
        public Task<bool> FailAsync(Guid w,Guid j,string o,string? e,CancellationToken t) => inner.FailAsync(w,j,o,e,t);
        public Task<bool> ReleaseAsync(Guid w,Guid j,string o,DateTimeOffset? a,CancellationToken t) => inner.ReleaseAsync(w,j,o,a,t);
        public Task<bool> CancelAsync(Guid w,Guid j,CancellationToken t) => inner.CancelAsync(w,j,t);
        public Task<int> RecoverAsync(Guid w,CancellationToken t) => inner.RecoverAsync(w,t);
    }
}
