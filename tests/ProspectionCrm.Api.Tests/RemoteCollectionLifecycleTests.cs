using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CollectionJobs;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Collection;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class RemoteCollectionLifecycleTests(AutomationJobDatabase database) : IClassFixture<AutomationJobDatabase>
{
    private sealed record Setup(SecurityTestHost Host, Guid Search, Guid Stage, Guid Source, Guid Pipeline);
    private sealed class SimulatedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private async Task<Setup> Prepare(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
    {
        var host = await SecurityTestHost.CreateAsync(database);
        host.TransportHandler = () => new SimulatedHandler(send ?? ((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted))));
        await using var db = database.Db();
        var pipeline = new Pipeline { WorkspaceId = host.Operator.WorkspaceId, Name = "Remote", TypeCode = "custom" };
        var stage = new PipelineStage { Pipeline = pipeline, Name = "Inbox", CategoryCode = "active" };
        var source = new SourceConfiguration { WorkspaceId = host.Operator.WorkspaceId, Name = "Phase 9 test", SourceTypeCode = CollectionIntegrationContracts.SourceType };
        var search = new SavedSearch { WorkspaceId = host.Operator.WorkspaceId, Pipeline = pipeline, SourceConfiguration = source,
            Name = "Remote request", CriteriaJson = "{\"query\":\"developer\",\"maxItems\":100}" };
        db.Add(stage); db.Add(search); await db.SaveChangesAsync();
        return new(host, search.Id, stage.Id, source.Id, pipeline.Id);
    }
    private static async Task<Guid> Enqueue(Setup s, HttpClient client)
    {
        using var response = await client.PostAsJsonAsync($"/api/saved-searches/{s.Search}/collection-jobs", new { pipelineStageId = s.Stage });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SourceCollectionJobDto>())!.Id;
    }
    private static async Task<bool> Process(SecurityTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobProcessor>().ProcessNextAsync(default);
    }
    private async Task<SourceCollectionJob> Job(Guid id)
    { await using var db = database.Db(); return await db.SourceCollectionJobs.AsNoTracking().SingleAsync(x => x.Id == id); }
    private async Task<RemoteCollectionIntent> Intent(Guid job)
    { await using var db = database.Db(); return await db.RemoteCollectionIntents.AsNoTracking().SingleAsync(x => x.JobId == job); }
    private async Task<byte[]> Result(Guid job, int descriptionSize = 0)
    {
        var intent = await Intent(job); var c = CollectionIntegrationContracts.ParseCommand(intent.CommandJson);
        return Encoding.UTF8.GetBytes(CollectionIntegrationContracts.Serialize(new CollectionResultEnvelope(c.Version, Guid.NewGuid(),
            c.OperationId, c.CorrelationId, c.MessageId, c.Type, new(c.Payload.RequestId, "succeeded",
                [new("external-test-1", "Candidate", Description: descriptionSize == 0 ? null : new string('d', descriptionSize))]))));
    }
    private static void Waiting(SourceCollectionJob job, string status = "awaiting-result")
    {
        Assert.Equal(status, job.StatusCode); Assert.NotNull(job.StartedAt); Assert.Null(job.FinishedAt);
        Assert.Null(job.LeaseToken); Assert.Null(job.LeaseExpiresAt); Assert.Null(job.SourceExecutionId);
        Assert.Equal(status == "dispatch-unknown" ? "RemoteDispatchUnknown" : null, job.ErrorCode);
    }
    private async Task NoBusiness(Guid workspace)
    {
        await using var db = database.Db();
        Assert.False(await db.Opportunities.AnyAsync(x => x.WorkspaceId == workspace));
        Assert.False(await db.Companies.AnyAsync(x => x.WorkspaceId == workspace));
        Assert.False(await db.SourceExecutions.AnyAsync(x => x.WorkspaceId == workspace));
    }

    [Fact]
    public async Task AckReleasesLeaseWithoutSuccessAndSurvivesRestartWithActiveUniquenessAndDto()
    {
        var calls = 0; var s = await Prepare((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)); });
        using var host = s.Host; using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var id = await Enqueue(s, client); Assert.True(await Process(host)); Waiting(await Job(id));
        var intent = await Intent(id); Assert.Equal(host.Operator.WorkspaceId, intent.WorkspaceId); Assert.Equal(host.Integration.ConnectionId, intent.ConnectionId);
        Assert.Equal(s.Source, intent.SourceConfigurationId); Assert.Equal(64, intent.Fingerprint.Length);
        using var snapshot = JsonDocument.Parse(intent.ContextSnapshotJson); Assert.Equal(s.Search, snapshot.RootElement.GetProperty("savedSearch").GetProperty("id").GetGuid());
        await using var db = database.Db();
        var attempt = await db.SourceCollectionJobAttempts.SingleAsync(x => x.JobId == id);
        Assert.Equal("awaiting-result", attempt.StatusCode); Assert.Null(attempt.FinishedAt); Assert.Null(attempt.ErrorCode);
        Assert.Equal("acknowledged", (await db.IntegrationAttempts.SingleAsync(x => x.OperationId == intent.OperationId)).StatusCode);
        var dto = await client.GetFromJsonAsync<SourceCollectionJobDto>($"/api/source-collection-jobs/{id}");
        Assert.Equal(intent.OperationId, dto!.IntegrationOperationId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/source-collection-jobs?statusCode=awaiting-result")).StatusCode);
        using var duplicate = await client.PostAsJsonAsync($"/api/saved-searches/{s.Search}/collection-jobs", new { pipelineStageId = s.Stage });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains(id.ToString(), await duplicate.Content.ReadAsStringAsync());
        // New service provider, same durable database; no callback deadline or lease recovery.
        using var restarted = await SecurityTestHost.CreateAsync(database, host.Operator.WorkspaceId);
        restarted.TransportHandler = () => new SimulatedHandler((_, _) => throw new Exception("No replay permitted"));
        Assert.False(await Process(restarted)); Waiting(await Job(id)); Assert.Equal(1, calls);
        await NoBusiness(host.Operator.WorkspaceId);
    }

    [Fact]
    public async Task ConcurrentWorkersHoldNoDatabaseTransactionDuringHttpAndOnlyOneDispatchOccurs()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var s = await Prepare(async (_, token) => { calls++; entered.TrySetResult(); await release.Task.WaitAsync(token); return new(HttpStatusCode.Accepted); });
        using var host = s.Host; host.Integration.TimeoutSeconds = 30; using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var id = await Enqueue(s, client); var processing = Process(host); await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            Assert.False(await Process(host));
            await using var db = database.Db(); await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"SourceCollectionJobs\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT");
            var intent = await Intent(id);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"IntegrationOperations\" WHERE \"Id\" = {intent.OperationId} FOR UPDATE NOWAIT");
            Assert.Equal(1, calls); await tx.CommitAsync();
        }
        finally { release.TrySetResult(); }
        Assert.True(await processing); Waiting(await Job(id)); await NoBusiness(host.Operator.WorkspaceId);
    }

    [Fact]
    public async Task DefiniteFailureRetriesSameOperationAndExactCommandWithNewTransportAttempt()
    {
        var commands = new ConcurrentQueue<string>();
        var s = await Prepare(async (request, token) =>
        { commands.Enqueue(await request.Content!.ReadAsStringAsync(token)); return new(commands.Count == 1 ? HttpStatusCode.BadRequest : HttpStatusCode.Accepted); });
        using var host = s.Host; using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var id = await Enqueue(s, client); Assert.True(await Process(host)); var queued = await Job(id);
        Assert.Equal("queued", queued.StatusCode); Assert.Null(queued.LeaseToken); Assert.Null(queued.StartedAt); Assert.Equal(1, queued.AttemptCount);
        await using var db = database.Db(); var intent = await Intent(id);
        var failed = await db.SourceCollectionJobAttempts.SingleAsync(x => x.JobId == id);
        Assert.Equal("RemoteDispatchFailed", failed.ErrorCode); Assert.NotNull(failed.FinishedAt);
        await db.SourceCollectionJobs.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableAt, queued.EnqueuedAt));
        Assert.True(await Process(host)); Waiting(await Job(id)); Assert.Equal(2, (await Job(id)).AttemptCount);
        Assert.Equal(2, commands.Count); Assert.Equal(commands.First(), commands.Last()); Assert.Equal(intent.OperationId, (await Intent(id)).OperationId);
        Assert.Equal(new[] { "failed", "acknowledged" }, await db.IntegrationAttempts.Where(x => x.OperationId == intent.OperationId).OrderBy(x => x.Number).Select(x => x.StatusCode).ToArrayAsync());
        await NoBusiness(host.Operator.WorkspaceId);
    }

    [Theory]
    [InlineData("disconnect")][InlineData("server-error")][InlineData("timeout")]
    public async Task UnknownNeverRedispatchesAndAValidCallbackResolvesItsDiagnosticState(string mode)
    {
        var calls = 0; var s = await Prepare(async (_, token) =>
        {
            calls++;
            if (mode == "timeout") await Task.Delay(Timeout.Infinite, token);
            if (mode == "disconnect") throw new HttpRequestException("synthetic disconnect");
            return new(HttpStatusCode.InternalServerError);
        });
        using var host = s.Host; using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var id = await Enqueue(s, client); Assert.True(await Process(host)); Waiting(await Job(id), "dispatch-unknown");
        Assert.False(await Process(host)); Assert.False(await Process(host)); Assert.Equal(1, calls);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(await Result(id)))).StatusCode);
        Waiting(await Job(id), "result-received"); Assert.False(await Process(host)); Assert.Equal(1, calls);
        await NoBusiness(host.Operator.WorkspaceId);
    }

    [Fact]
    public async Task DurableReceiptReplaysAcrossRestartAndCannotBeProcessedOrSentThroughProbeRoutes()
    {
        var s = await Prepare(); using var host = s.Host; using var human = host.Client(); await SecurityTestHost.LoginAsync(human);
        var id = await Enqueue(s, human); await Process(host); var body = await Result(id, 9000); var intent = await Intent(id);
        using var machine = host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await human.PostAsync("/api/integrations/results", new ByteArrayContent(body))).StatusCode);
        var replies = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => machine.SendAsync(host.Signed(body))));
        Assert.All(replies, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Waiting(await Job(id), "result-received");
        using var restarted = new SecurityTestHost(database.Postgres.GetConnectionString(), host.Operator.UserId, host.Operator.WorkspaceId);
        // Reception uses the same logical connection and synthetic keys after a new API process.
        restarted.Operator.PasswordHash = host.Operator.PasswordHash;
        restarted.Integration.ConnectionId = host.Integration.ConnectionId; restarted.Integration.OutgoingKey = host.Integration.OutgoingKey;
        restarted.Integration.IncomingKeys = host.Integration.IncomingKeys;
        using var client = restarted.Client(); await SecurityTestHost.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(restarted.Signed(body))).StatusCode);
        Assert.False(await Process(restarted));
        await using var db = database.Db(); var receipt = await db.IntegrationReceipts.SingleAsync(x => x.OperationId == intent.OperationId);
        Assert.Null(receipt.ProcessedAt); Assert.Equal(9000, CollectionIntegrationContracts.ParseResult(receipt.CanonicalContent).Payload.Items[0].Description!.Length);
        foreach (var route in new[] { "send", "process" })
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/integrations/probes/{intent.OperationId}/{route}", new { revision = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/integrations/probes/{intent.OperationId}")).StatusCode);
        await NoBusiness(host.Operator.WorkspaceId);
    }

    [Theory]
    [InlineData("awaiting-result")][InlineData("dispatch-unknown")][InlineData("result-received")]
    public async Task LocalCancellationClosesAttemptAndLateCallbackOnlyRetainsTrace(string state)
    {
        var s = await Prepare((_, _) => Task.FromResult(new HttpResponseMessage(state == "dispatch-unknown" ? HttpStatusCode.InternalServerError : HttpStatusCode.Accepted)));
        using var host = s.Host; using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var id = await Enqueue(s, client); await Process(host); var body = await Result(id);
        if (state == "result-received") Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(body))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/source-collection-jobs/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/source-collection-jobs/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(body))).StatusCode);
        var cancelled = await Job(id); Assert.Equal("cancelled", cancelled.StatusCode); Assert.NotNull(cancelled.FinishedAt);
        await using var db = database.Db(); var attempt = await db.SourceCollectionJobAttempts.SingleAsync(x => x.JobId == id);
        Assert.Equal("cancelled", attempt.StatusCode); Assert.NotNull(attempt.FinishedAt); Assert.Null(attempt.SourceExecutionId);
        Assert.False(await Process(host)); await NoBusiness(host.Operator.WorkspaceId);
    }

    [Theory]
    [InlineData("search")][InlineData("source")][InlineData("stage")][InlineData("pipeline")][InlineData("workspace")][InlineData("type")]
    public async Task ChangedFrozenContextRejectsResultWithoutPersistingOrIngesting(string change)
    {
        var s = await Prepare(); using var host = s.Host; using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var id = await Enqueue(s, client); await Process(host); var body = await Result(id); await using var db = database.Db();
        switch (change)
        {
            case "search": await db.SavedSearches.Where(x => x.Id == s.Search).ExecuteUpdateAsync(x => x.SetProperty(p => p.CriteriaJson, "{\"query\":\"changed\"}")); break;
            case "source": await db.SourceConfigurations.Where(x => x.Id == s.Source).ExecuteUpdateAsync(x => x.SetProperty(p => p.ConfigurationJson, "{\"changed\":true}")); break;
            case "stage": await db.PipelineStages.Where(x => x.Id == s.Stage).ExecuteUpdateAsync(x => x.SetProperty(p => p.Name, "changed")); break;
            case "pipeline": await db.Pipelines.Where(x => x.Id == s.Pipeline).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "workspace": await db.Workspaces.Where(x => x.Id == host.Operator.WorkspaceId).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "type": await db.SourceConfigurations.Where(x => x.Id == s.Source).ExecuteUpdateAsync(x => x.SetProperty(p => p.SourceTypeCode, "rss")); break;
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(host.Signed(body))).StatusCode);
        var intent = await Intent(id); Assert.False(await db.IntegrationReceipts.AnyAsync(x => x.OperationId == intent.OperationId));
        Waiting(await Job(id)); await NoBusiness(host.Operator.WorkspaceId);
    }

    [Theory]
    [InlineData("wrong-workspace")][InlineData("wrong-connection")]
    public async Task UnauthorizedKeysCannotUseInitialReplayOrConflictPaths(string key)
    {
        var s = await Prepare(); using var host = s.Host; using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var id = await Enqueue(s, client); await Process(host); var body = await Result(id);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(host.Signed(body, key))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(body))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(host.Signed(body, key))).StatusCode);
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("Candidate", "Different"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(host.Signed(changed, key))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(host.Signed(changed))).StatusCode);
        await NoBusiness(host.Operator.WorkspaceId);
    }

    [Theory]
    [InlineData("operationId")][InlineData("correlationId")][InlineData("commandMessageId")][InlineData("requestId")]
    public async Task ResultCannotChooseAnotherOperationOrRequest(string field)
    {
        var s = await Prepare(); using var host = s.Host; using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var id = await Enqueue(s, client); await Process(host);
        var root = System.Text.Json.Nodes.JsonNode.Parse(await Result(id))!;
        if (field == "requestId") root["payload"]![field] = Guid.NewGuid(); else root[field] = Guid.NewGuid();
        Assert.Equal(field == "operationId" ? HttpStatusCode.Forbidden : HttpStatusCode.Conflict,
            (await client.SendAsync(host.Signed(Encoding.UTF8.GetBytes(root.ToJsonString())))).StatusCode);
        Waiting(await Job(id)); await NoBusiness(host.Operator.WorkspaceId);
    }

    [Theory]
    [InlineData("none", "queued")][InlineData("started", "dispatch-unknown")]
    [InlineData("acknowledged", "awaiting-result")][InlineData("failed", "queued")]
    public async Task CrashRecoveryUsesTransportEvidenceAndNeverCreatesAnotherLogicalOperation(string transportState, string expected)
    {
        var calls = 0; var s = await Prepare((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)); });
        using var host = s.Host; using var client = host.Client(); await SecurityTestHost.LoginAsync(client); var id = await Enqueue(s, client);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<ISourceCollectionJobQueue>(); var job = (await queue.ClaimAsync(default))!;
            Assert.Equal(id, job.Id); var state = scope.ServiceProvider.GetRequiredService<RemoteCollectionState>();
            var intent = await state.PrepareAsync(job, host.Integration.ConnectionId, default);
            if (transportState != "none")
            {
                var attempt = await state.BeginAsync(job, intent, default);
                if (transportState != "started") await scope.ServiceProvider.GetRequiredService<IntegrationStore>().FinishAttemptAsync(job.WorkspaceId,
                    intent.OperationId, attempt.Id, transportState == "failed" ? IntegrationTransportOutcome.Failed : IntegrationTransportOutcome.Acknowledged);
            }
        }
        await using var db = database.Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SourceCollectionJobs" SET "EnqueuedAt" = statement_timestamp() - interval '20 minutes',
                "AvailableAt" = statement_timestamp() - interval '20 minutes', "StartedAt" = statement_timestamp() - interval '10 minutes',
                "LeaseExpiresAt" = statement_timestamp() - interval '1 minute' WHERE "Id" = {id}
            """);
        Assert.True(await Process(host)); Assert.Equal(expected, (await Job(id)).StatusCode); Assert.Equal(0, calls);
        var original = await Intent(id);
        if (expected == "queued")
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"SourceCollectionJobs\" SET \"AvailableAt\" = statement_timestamp() WHERE \"Id\" = {id}");
            Assert.True(await Process(host)); Waiting(await Job(id)); Assert.Equal(1, calls); Assert.Equal(original.OperationId, (await Intent(id)).OperationId);
        }
        else { Assert.False(await Process(host)); Waiting(await Job(id), expected); }
        await NoBusiness(host.Operator.WorkspaceId);
    }

    [Fact]
    public async Task CallbackBeforeAckSurvivesCrashAndDetachedTransportRecoveryDoesNotReopenCancelledJob()
    {
        var s = await Prepare(); using var host = s.Host; using var client = host.Client(); await SecurityTestHost.LoginAsync(client); var id = await Enqueue(s, client);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var job = (await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobQueue>().ClaimAsync(default))!;
            var state = scope.ServiceProvider.GetRequiredService<RemoteCollectionState>();
            var intent = await state.PrepareAsync(job, host.Integration.ConnectionId, default); await state.BeginAsync(job, intent, default);
        }
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(await Result(id)))).StatusCode);
        Waiting(await Job(id), "result-received");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/source-collection-jobs/{id}/cancel", null)).StatusCode);
        Assert.True(await Process(host)); Assert.False(await Process(host)); Assert.Equal("cancelled", (await Job(id)).StatusCode);
        var op = (await Intent(id)).OperationId; await using var db = database.Db();
        Assert.Equal("unknown", (await db.IntegrationAttempts.SingleAsync(x => x.OperationId == op)).StatusCode);
        Assert.Null((await db.IntegrationReceipts.SingleAsync(x => x.OperationId == op)).ProcessedAt);
        await NoBusiness(host.Operator.WorkspaceId);
    }

    [Fact]
    public async Task DisabledIntegrationCannotPersistIntentOrSendAndSynchronousEndpointRejectsRemoteSource()
    {
        var s = await Prepare((_, _) => throw new Exception("Disabled transport")); using var host = s.Host; host.Integration.Enabled = false;
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client); var id = await Enqueue(s, client);
        await Process(host); Assert.Equal("RemoteIntegrationDisabled", (await Job(id)).ErrorCode);
        await using var db = database.Db(); Assert.False(await db.RemoteCollectionIntents.AnyAsync(x => x.JobId == id));
        await using var scope = host.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISourceCollectionOrchestrator>().CollectAsync(host.Operator.WorkspaceId, s.Search, s.Stage, null, default);
        Assert.Equal("RemoteCollectionRequiresJob", result.Error!.Code); await NoBusiness(host.Operator.WorkspaceId);
    }

    [Fact]
    public async Task SchedulerCoalescesAllThreeRemoteActiveStates()
    {
        var s = await Prepare((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var host = s.Host; using var client = host.Client(); await SecurityTestHost.LoginAsync(client); var id = await Enqueue(s, client);
        await Process(host); Waiting(await Job(id), "dispatch-unknown");
        await using var db = database.Db();
        db.Add(new SourceCollectionSchedule { SavedSearchId = s.Search, WorkspaceId = host.Operator.WorkspaceId, PipelineId = s.Pipeline,
            PipelineStageId = s.Stage, Enabled = true, DailyUtcMinute = 0, NextCollectionAt = DateTimeOffset.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        foreach (var status in new[] { "dispatch-unknown", "awaiting-result", "result-received" })
        {
            if (status == "awaiting-result")
                await db.SourceCollectionJobs.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(j => j.StatusCode, status).SetProperty(j => j.ErrorCode, (string?)null));
            if (status == "result-received") Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(await Result(id)))).StatusCode);
            await db.SourceCollectionSchedules.Where(x => x.SavedSearchId == s.Search).ExecuteUpdateAsync(x => x.SetProperty(c => c.NextCollectionAt, DateTimeOffset.UtcNow.AddDays(-1)));
            await using var scope = host.Services.CreateAsyncScope();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ISourceCollectionSchedulingService>().ScheduleDueAsync(default));
            Assert.Equal(1, await db.SourceCollectionJobs.CountAsync(x => x.SavedSearchId == s.Search));
            Assert.True(await db.SourceCollectionSchedules.AnyAsync(x => x.SavedSearchId == s.Search && x.NextCollectionAt > DateTimeOffset.UtcNow));
        }
        await NoBusiness(host.Operator.WorkspaceId);
    }

    [Fact]
    public async Task CallbackCanArriveDuringHttpAndLiveGuardPreventsPrematureUnknownRecovery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s = await Prepare(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return new(HttpStatusCode.Accepted); });
        using var host = s.Host; host.Integration.TimeoutSeconds = 30; using var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var id = await Enqueue(s, client); var processing = Process(host); await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(await Result(id)))).StatusCode);
            Waiting(await Job(id), "result-received"); Assert.False(await Process(host));
            await using var db = database.Db(); var intent = await Intent(id);
            Assert.Equal("started", (await db.IntegrationAttempts.SingleAsync(x => x.OperationId == intent.OperationId)).StatusCode);
        }
        finally { release.TrySetResult(); }
        Assert.True(await processing); Waiting(await Job(id), "result-received"); Assert.False(await Process(host));
        await using var check = database.Db(); var op = (await Intent(id)).OperationId;
        Assert.Equal("acknowledged", (await check.IntegrationAttempts.SingleAsync(x => x.OperationId == op)).StatusCode);
        await NoBusiness(host.Operator.WorkspaceId);
    }

    [Theory]
    [InlineData("context")][InlineData("connection")]
    public async Task FailedDispatchCannotRetryAgainstChangedContextOrLogicalConnection(string change)
    {
        var calls = 0; var s = await Prepare((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)); });
        using var host = s.Host; using var client = host.Client(); await SecurityTestHost.LoginAsync(client); var id = await Enqueue(s, client);
        await Process(host); var intent = await Intent(id);
        await using var db = database.Db();
        if (change == "context") await db.SavedSearches.Where(x => x.Id == s.Search).ExecuteUpdateAsync(x => x.SetProperty(j => j.Name, "Changed"));
        if (change == "connection")
        {
            // Valid replacement configuration, but it cannot take over an older intention.
            using var replacement = await SecurityTestHost.CreateAsync(database, host.Operator.WorkspaceId);
            replacement.TransportHandler = () => new SimulatedHandler((_, _) => throw new Exception("Connection must be rejected before transport"));
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"SourceCollectionJobs\" SET \"AvailableAt\" = statement_timestamp() WHERE \"Id\" = {id}");
            Assert.True(await Process(replacement));
        }
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"SourceCollectionJobs\" SET \"AvailableAt\" = statement_timestamp() WHERE \"Id\" = {id}");
            Assert.True(await Process(host));
        }
        var failed = await Job(id); Assert.Equal("failed", failed.StatusCode);
        Assert.Equal(change == "context" ? "RemoteContextChanged" : "RemoteConnectionMismatch", failed.ErrorCode);
        Assert.Equal(1, calls); Assert.Equal(intent.OperationId, (await Intent(id)).OperationId);
        Assert.Equal(1, await db.IntegrationAttempts.CountAsync(x => x.OperationId == intent.OperationId)); await NoBusiness(host.Operator.WorkspaceId);
    }

    [Theory]
    [InlineData("signature", 401)][InlineData("too-large", 413)][InlineData("too-many", 400)][InlineData("criteria-limit", 409)]
    public async Task MachineReceptionEnforcesSignatureBodyAndRequestedBatchLimits(string change, int expected)
    {
        var s = await Prepare(); using var host = s.Host;
        await using var db = database.Db();
        if (change == "criteria-limit") await db.SavedSearches.Where(x => x.Id == s.Search).ExecuteUpdateAsync(x => x.SetProperty(j => j.CriteriaJson, "{\"maxItems\":1}"));
        using var client = host.Client(); await SecurityTestHost.LoginAsync(client); var id = await Enqueue(s, client); await Process(host);
        var body = await Result(id);
        if (change == "too-large") body = [.. body, .. Encoding.UTF8.GetBytes(new string(' ', CollectionIntegrationContracts.MaxResultBytes))];
        if (change is "too-many" or "criteria-limit")
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(body)!;
            root["payload"]!["items"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, change == "too-many" ? 101 : 2)
                .Select(i => new { externalId = $"candidate-{i}", title = "Candidate" }));
            body = Encoding.UTF8.GetBytes(root.ToJsonString());
        }
        using var request = host.Signed(body);
        if (change == "signature") { request.Headers.Remove(IntegrationSignature.SignatureHeader); request.Headers.Add(IntegrationSignature.SignatureHeader, new string('0', 64)); }
        Assert.Equal(expected, (int)(await client.SendAsync(request)).StatusCode);
        var intent = await Intent(id); Assert.False(await db.IntegrationReceipts.AnyAsync(x => x.OperationId == intent.OperationId));
        Waiting(await Job(id)); await NoBusiness(host.Operator.WorkspaceId);
    }
}
