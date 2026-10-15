using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CollectionJobs;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Collection;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class RemoteCollectionResultProcessingTests(AutomationJobDatabase database) : IClassFixture<AutomationJobDatabase>
{
    private sealed record Setup(SecurityTestHost Host, HttpClient Client, Guid Search, Guid Source, Guid Pipeline, Guid Stage,
        Guid Job, byte[] Body) : IDisposable
    {
        public Guid Workspace => Host.Operator.WorkspaceId;
        public void Dispose() { Client.Dispose(); Host.Dispose(); }
    }
    private sealed class SimulatedTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
    }
    private sealed class FixedWorkspace(Guid workspace) : ICurrentWorkspaceProvider
    { public Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken token = default) => Task.FromResult(workspace); }
    private static CollectionCandidate[] Candidates() =>
    [
        new("remote-demo-001", "Remote demo 001", "https://example.invalid/remote/001", "Paris", "First deterministic candidate"),
        new("remote-demo-002", "Remote demo 002", "https://example.invalid/remote/002", "Lyon", "Second deterministic candidate")
    ];
    private ProspectionCrmDbContext Db(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
        .UseNpgsql(database.Postgres.GetConnectionString()).AddInterceptors(interceptors).Options);
    private RemoteCollectionResultProcessor Processor(Setup s, ProspectionCrmDbContext db)
    {
        var workspace = new FixedWorkspace(s.Workspace);
        var resolver = new SourceCollectionContextResolver(db, new SourceAdapterRegistry([]));
        return new(db, workspace, new(db, resolver), new IngestionService(db, workspace, NullLogger<IngestionService>.Instance),
            s.Host.Services.GetRequiredService<IOptionsMonitor<IntegrationOptions>>());
    }
    private SourceCollectionJobService Jobs(Setup s, ProspectionCrmDbContext db) =>
        new(db, new FixedWorkspace(s.Workspace), new SourceCollectionContextResolver(db, new SourceAdapterRegistry([])));
    private async Task<Setup> Prepare(bool receive = true, string outcome = "succeeded", CollectionCandidate[]? items = null, bool finishTransport = true)
    {
        var host = await SecurityTestHost.CreateAsync(database);
        host.TransportHandler = () => new SimulatedTransport();
        await using var db = Db();
        var pipeline = new Pipeline { WorkspaceId = host.Operator.WorkspaceId, Name = "Remote processing", TypeCode = "custom" };
        var stage = new PipelineStage { Pipeline = pipeline, Name = "Inbox", CategoryCode = "active" };
        var source = new SourceConfiguration { WorkspaceId = host.Operator.WorkspaceId, Name = "Remote demo source", SourceTypeCode = CollectionIntegrationContracts.SourceType };
        var search = new SavedSearch { WorkspaceId = host.Operator.WorkspaceId, Pipeline = pipeline, SourceConfiguration = source,
            Name = "Remote demo search", CriteriaJson = """{"query":"developer","maxItems":100}""" };
        db.Add(stage); db.Add(search); await db.SaveChangesAsync();
        var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        return await Dispatch(new(host, client, search.Id, source.Id, pipeline.Id, stage.Id, Guid.Empty, []), receive, outcome, items, finishTransport);
    }
    private async Task<Setup> Dispatch(Setup s, bool receive = true, string outcome = "succeeded", CollectionCandidate[]? items = null, bool finishTransport = true)
    {
        using var enqueued = await s.Client.PostAsJsonAsync($"/api/saved-searches/{s.Search}/collection-jobs", new { pipelineStageId = s.Stage });
        Assert.Equal(HttpStatusCode.Accepted, enqueued.StatusCode);
        var job = (await enqueued.Content.ReadFromJsonAsync<SourceCollectionJobDto>())!.Id;
        await using (var scope = s.Host.Services.CreateAsyncScope())
        {
            if (finishTransport) Assert.True(await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobProcessor>().ProcessNextAsync(default));
            else
            {
                var claimed = (await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobQueue>().ClaimAsync(default))!;
                Assert.Equal(job, claimed.Id);
                var state = scope.ServiceProvider.GetRequiredService<RemoteCollectionState>();
                var prepared = await state.PrepareAsync(claimed, s.Host.Integration.ConnectionId, default);
                await state.BeginAsync(claimed, prepared, default);
            }
        }
        await using var db = Db();
        var intent = await db.RemoteCollectionIntents.SingleAsync(x => x.JobId == job);
        var command = CollectionIntegrationContracts.ParseCommand(intent.CommandJson);
        var body = Encoding.UTF8.GetBytes(CollectionIntegrationContracts.Serialize(new CollectionResultEnvelope(1, Guid.NewGuid(),
            command.OperationId, command.CorrelationId, command.MessageId, command.Type,
            new(command.Payload.RequestId, outcome, items ?? (outcome == "failed" ? [] : Candidates())))));
        if (receive) Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(body))).StatusCode);
        return s with { Job = job, Body = body };
    }
    private static string ProcessUrl(Guid job) => $"/api/source-collection-jobs/{job}/process-result";
    private async Task<SourceCollectionJobResult<bool>> Process(Setup s, params IInterceptor[] interceptors)
    {
        await using var db = Db(interceptors);
        return await Processor(s, db).ProcessAsync(s.Job, default).WaitAsync(TimeSpan.FromSeconds(30));
    }
    private async Task Terminal(Setup s, string status, string? error, bool execution)
    {
        await using var db = Db();
        var job = await db.SourceCollectionJobs.SingleAsync(x => x.Id == s.Job);
        var attempt = await db.SourceCollectionJobAttempts.SingleAsync(x => x.JobId == s.Job);
        var intent = await db.RemoteCollectionIntents.SingleAsync(x => x.JobId == s.Job);
        var operation = await db.IntegrationOperations.SingleAsync(x => x.Id == intent.OperationId);
        var receipt = await db.IntegrationReceipts.SingleAsync(x => x.OperationId == operation.Id);
        Assert.Equal(status, job.StatusCode); Assert.Equal(status, attempt.StatusCode); Assert.Equal(status, operation.StatusCode);
        Assert.Equal(error, job.ErrorCode); Assert.Equal(error, attempt.ErrorCode); Assert.Equal(1, job.AttemptCount);
        Assert.NotNull(job.FinishedAt); Assert.NotNull(attempt.FinishedAt); Assert.NotNull(operation.CompletedAt); Assert.NotNull(receipt.ProcessedAt);
        Assert.Null(job.LeaseToken); Assert.Null(job.LeaseExpiresAt);
        Assert.Equal(execution, job.SourceExecutionId.HasValue); Assert.Equal(job.SourceExecutionId, attempt.SourceExecutionId);
        Assert.Empty(await new IntegrationStore(db, TimeProvider.System).PendingReceiptsAsync(s.Workspace));
        Assert.False(await db.Companies.AnyAsync(x => x.WorkspaceId == s.Workspace));
        Assert.False(await db.EmailMessages.AnyAsync(x => x.WorkspaceId == s.Workspace));
        Assert.False(await db.CalendarEvents.AnyAsync(x => x.WorkspaceId == s.Workspace));
        Assert.False(await db.AutomationJobs.AnyAsync(x => x.WorkspaceId == s.Workspace));
    }
    private async Task Unprocessed(Setup s, string status = "result-received")
    {
        await using var db = Db();
        Assert.Equal(status, (await db.SourceCollectionJobs.SingleAsync(x => x.Id == s.Job)).StatusCode);
        Assert.False(await db.SourceExecutions.AnyAsync(x => x.WorkspaceId == s.Workspace));
        Assert.False(await db.Opportunities.AnyAsync(x => x.WorkspaceId == s.Workspace));
        var operation = (await db.RemoteCollectionIntents.SingleAsync(x => x.JobId == s.Job)).OperationId;
        Assert.Null((await db.IntegrationReceipts.SingleAsync(x => x.OperationId == operation)).ProcessedAt);
    }

    [Fact]
    public async Task TwoCandidatesHaveCompleteProvenanceAndHistoryAndNewCollectionOnlyRediscoversThem()
    {
        using var s = await Prepare();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsync(ProcessUrl(s.Job), null)).StatusCode);
        await Terminal(s, "succeeded", null, true);
        await using var db = Db();
        var execution = await db.SourceExecutions.Include(x => x.Items).ThenInclude(x => x.Sources).SingleAsync(x => x.WorkspaceId == s.Workspace);
        Assert.Equal((2, 2, 0, 0), (execution.ItemsFound, execution.ItemsCreated, execution.ItemsUpdated, execution.ItemsIgnored));
        Assert.Equal("manual", execution.TriggerTypeCode); Assert.Equal(s.Search, execution.SavedSearchId);
        Assert.Equal(s.Source, execution.SourceConfigurationId); Assert.Equal(s.Pipeline, execution.TargetPipelineId); Assert.Equal(s.Stage, execution.TargetPipelineStageId);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse((await db.RemoteCollectionIntents.SingleAsync(x => x.JobId == s.Job)).ContextSnapshotJson),
            System.Text.Json.Nodes.JsonNode.Parse(execution.ContextSnapshotJson!)));
        var sources = await db.OpportunitySources.AsNoTracking().Where(x => x.WorkspaceId == s.Workspace).OrderBy(x => x.ExternalId).ToArrayAsync();
        Assert.Equal(2, sources.Length);
        var opportunities = await db.Opportunities.AsNoTracking().Where(x => x.WorkspaceId == s.Workspace).OrderBy(x => x.Title).ToArrayAsync();
        Assert.Equal(2, opportunities.Length);
        foreach (var (item, index) in execution.Items.OrderBy(x => x.ItemIndex).Select((x, i) => (x, i)))
        {
            var candidate = Candidates()[index]; var source = sources[index];
            Assert.Equal(candidate.ExternalId, item.ExternalId); Assert.Equal(candidate.Title, item.Title);
            Assert.Equal(IngestionNormalization.TextKey(candidate.Title), item.NormalizedTitle);
            Assert.Equal(candidate.SourceUrl, item.SourceUrl); Assert.Equal(IngestionNormalization.UrlKey(candidate.SourceUrl), item.NormalizedSourceUrl);
            Assert.Null(item.CompanyName); Assert.Null(item.NormalizedCompanyName);
            Assert.Equal("created", item.OutcomeCode); Assert.Equal(IngestionHistoryCodes.CreatedNewOpportunity, item.DecisionCode);
            Assert.Equal(item.OpportunityId, item.OpportunityIdSnapshot); Assert.Equal(source.OpportunityId, item.OpportunityId);
            Assert.Equal(2, item.Sources.Count); Assert.All(item.Sources, x => Assert.Equal(source.Id, x.OpportunitySourceIdSnapshot));
            using var payload = JsonDocument.Parse(item.PayloadSnapshotJson!);
            Assert.Equal(candidate.Location, payload.RootElement.GetProperty("location").GetString());
            Assert.Equal(candidate.Description, payload.RootElement.GetProperty("description").GetString());
            Assert.Equal(s.Source, source.SourceConfigurationId); Assert.Equal(s.Search, source.SavedSearchId);
            Assert.Equal(execution.Id, source.SourceExecutionId); Assert.Equal(candidate.ExternalId, source.ExternalId);
            Assert.Equal("Remote demo source", source.SourceLabel); Assert.Equal(source.FirstSeenAt, source.LastSeenAt);
            Assert.Null(opportunities[index].CompanyId); Assert.Equal(candidate.Location, opportunities[index].Location);
            Assert.Equal(candidate.Description, opportunities[index].Notes); Assert.Equal(s.Stage, opportunities[index].PipelineStageId);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(ProcessUrl(s.Job), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(s.Body))).StatusCode);
        Assert.Equal(1, await db.SourceExecutions.CountAsync(x => x.WorkspaceId == s.Workspace));
        var second = await Dispatch(s);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsync(ProcessUrl(second.Job), null)).StatusCode);
        await Terminal(second, "succeeded", null, true);
        Assert.Equal(2, await db.Opportunities.CountAsync(x => x.WorkspaceId == s.Workspace));
        var rediscovered = await db.SourceExecutions.Include(x => x.Items).SingleAsync(x => x.WorkspaceId == s.Workspace && x.Id != execution.Id);
        Assert.Equal((2, 0, 2, 0), (rediscovered.ItemsFound, rediscovered.ItemsCreated, rediscovered.ItemsUpdated, rediscovered.ItemsIgnored));
        Assert.All(rediscovered.Items, x => { Assert.Equal("updated", x.OutcomeCode); Assert.Equal(IngestionHistoryCodes.MatchedConsistentIdentities, x.DecisionCode); });
        var after = await db.OpportunitySources.AsNoTracking().Where(x => x.WorkspaceId == s.Workspace).OrderBy(x => x.ExternalId).ToArrayAsync();
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(sources[i].Id, after[i].Id); Assert.Equal(sources[i].SourceExecutionId, after[i].SourceExecutionId);
            Assert.Equal(sources[i].FirstSeenAt, after[i].FirstSeenAt); Assert.True(after[i].LastSeenAt > sources[i].LastSeenAt);
        }
        var dto = await s.Client.GetFromJsonAsync<JsonElement>($"/api/source-collection-jobs/{second.Job}");
        Assert.Equal("succeeded", dto.GetProperty("statusCode").GetString());
        Assert.Equal(rediscovered.Id, dto.GetProperty("sourceExecutionId").GetGuid()); Assert.NotEqual(Guid.Empty, dto.GetProperty("integrationOperationId").GetGuid());
        foreach (var forbidden in new[] { "canonical", "snapshot", "secret", "payload", "command" }) Assert.DoesNotContain(forbidden, dto.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("search-disabled")][InlineData("search-archived")][InlineData("source-disabled")][InlineData("source-archived")]
    [InlineData("pipeline")][InlineData("stage")][InlineData("fingerprint")][InlineData("source-type")][InlineData("workspace")][InlineData("connection")]
    public async Task ContextIsRevalidatedAfterReceiptAndFailureIsTerminalWithoutIngestion(string change)
    {
        using var s = await Prepare(); await using var db = Db();
        switch (change)
        {
            case "search-disabled": await db.SavedSearches.Where(x => x.Id == s.Search).ExecuteUpdateAsync(x => x.SetProperty(p => p.Enabled, false)); break;
            case "search-archived": await db.SavedSearches.Where(x => x.Id == s.Search).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "source-disabled": await db.SourceConfigurations.Where(x => x.Id == s.Source).ExecuteUpdateAsync(x => x.SetProperty(p => p.Enabled, false)); break;
            case "source-archived": await db.SourceConfigurations.Where(x => x.Id == s.Source).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "pipeline": await db.Pipelines.Where(x => x.Id == s.Pipeline).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "stage": await db.PipelineStages.Where(x => x.Id == s.Stage).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "fingerprint": await db.SavedSearches.Where(x => x.Id == s.Search).ExecuteUpdateAsync(x => x.SetProperty(p => p.CriteriaJson, """{"query":"changed"}""")); break;
            case "source-type": await db.SourceConfigurations.Where(x => x.Id == s.Source).ExecuteUpdateAsync(x => x.SetProperty(p => p.SourceTypeCode, "rss")); break;
            case "workspace": await db.Workspaces.Where(x => x.Id == s.Workspace).ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "connection": s.Host.Services.GetRequiredService<IOptionsMonitor<IntegrationOptions>>().CurrentValue.ConnectionId = Guid.NewGuid(); break;
        }
        // Direct service scope represents a mutation after HTTP identity validation; an
        // already archived workspace is rejected even earlier by cookie authentication.
        var result = await Process(s); Assert.Equal(SourceCollectionJobStatus.Conflict, result.Status);
        await Terminal(s, "failed", change == "connection" ? "RemoteConnectionMismatch" : "RemoteContextChanged", false);
        Assert.False(await db.Opportunities.AnyAsync(x => x.WorkspaceId == s.Workspace));
        Assert.False(await db.SourceExecutions.AnyAsync(x => x.WorkspaceId == s.Workspace));
        Assert.Equal(SourceCollectionJobStatus.Conflict, (await Process(s)).Status);
    }

    [Fact]
    public async Task FailedRemoteOutcomeClosesWithoutIngestionAndTransportMayBeDisabledDuringProcessing()
    {
        using var s = await Prepare(outcome: "failed");
        s.Host.Services.GetRequiredService<IOptionsMonitor<IntegrationOptions>>().CurrentValue.Enabled = false;
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsync(ProcessUrl(s.Job), null)).StatusCode);
        await Terminal(s, "failed", "RemoteCollectionFailed", false);
        await using var db = Db(); Assert.False(await db.SourceExecutions.AnyAsync(x => x.WorkspaceId == s.Workspace));
        Assert.False(await db.Opportunities.AnyAsync(x => x.WorkspaceId == s.Workspace));
    }

    [Theory]
    [InlineData("cancelled")][InlineData("failed")]
    public async Task TerminalJobNeverIngestsItsDurableReceipt(string state)
    {
        using var s = await Prepare();
        if (state == "cancelled") Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsync($"/api/source-collection-jobs/{s.Job}/cancel", null)).StatusCode);
        else
        {
            await using var db = Db();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "SourceCollectionJobs" SET "StatusCode"='failed', "ErrorCode"='RemoteContextChanged', "FinishedAt"=statement_timestamp() WHERE "Id"={s.Job};
                UPDATE "SourceCollectionJobAttempts" SET "StatusCode"='failed', "ErrorCode"='RemoteContextChanged', "FinishedAt"=statement_timestamp() WHERE "JobId"={s.Job};
                """);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(ProcessUrl(s.Job), null)).StatusCode);
        await Unprocessed(s, state);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(s.Body))).StatusCode);
    }

    [Theory]
    [InlineData("anonymous")][InlineData("machine")][InlineData("csrf")][InlineData("body")][InlineData("workspace")]
    public async Task ProcessingRouteRequiresHumanCsrfAndAcceptsNoSenderItems(string mode)
    {
        using var s = await Prepare(); HttpResponseMessage response;
        if (mode == "body") response = await s.Client.PostAsJsonAsync(ProcessUrl(s.Job), new { items = Candidates(), pipelineStageId = Guid.NewGuid() });
        else if (mode == "csrf")
        {
            s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            response = await s.Client.PostAsync(ProcessUrl(s.Job), null);
        }
        else if (mode == "workspace")
        {
            using var other = await SecurityTestHost.CreateAsync(database); using var client = other.Client(); await SecurityTestHost.LoginAsync(client);
            response = await client.PostAsync(ProcessUrl(s.Job), null);
        }
        else
        {
            using var client = s.Host.Client();
            response = mode == "machine" ? await client.SendAsync(s.Host.Signed([], target: ProcessUrl(s.Job))) : await client.PostAsync(ProcessUrl(s.Job), null);
        }
        using (response) Assert.Equal(mode switch { "body" or "csrf" => 400, "workspace" => 404, _ => 401 }, (int)response.StatusCode);
        await Unprocessed(s);
    }

    [Fact]
    public async Task AwaitingResultAndUnknownCannotBeProcessedWithoutReceipt()
    {
        using var s = await Prepare(receive: false);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(ProcessUrl(s.Job), null)).StatusCode);
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SourceCollectionJobs" SET "StatusCode"='dispatch-unknown', "ErrorCode"='RemoteDispatchUnknown' WHERE "Id"={s.Job};
            UPDATE "SourceCollectionJobAttempts" SET "StatusCode"='dispatch-unknown', "ErrorCode"='RemoteDispatchUnknown' WHERE "JobId"={s.Job};
            """);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(ProcessUrl(s.Job), null)).StatusCode);
        Assert.False(await db.SourceExecutions.AnyAsync(x => x.WorkspaceId == s.Workspace));
    }

    [Theory]
    [InlineData("hash")][InlineData("request")][InlineData("destination")][InlineData("command")]
    public async Task PersistedEnvelopeIsDefensivelyValidatedBeforeIngestion(string corruption)
    {
        using var s = await Prepare(); await using var db = Db();
        var intent = await db.RemoteCollectionIntents.SingleAsync(x => x.JobId == s.Job);
        var receipt = await db.IntegrationReceipts.SingleAsync(x => x.OperationId == intent.OperationId);
        if (corruption == "hash") receipt.ContentSha256 = new string('0', 64);
        else if (corruption == "command") intent.CommandJson = "{}";
        else
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(receipt.CanonicalContent)!;
            if (corruption == "request") json["payload"]!["requestId"] = Guid.NewGuid();
            else json["pipelineStageId"] = Guid.NewGuid();
            receipt.CanonicalContent = json.ToJsonString();
            receipt.ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(receipt.CanonicalContent)));
        }
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(ProcessUrl(s.Job), null)).StatusCode);
        await Terminal(s, "failed", "RemoteResultInvalid", false);
        Assert.False(await db.Opportunities.AnyAsync(x => x.WorkspaceId == s.Workspace));
    }

    [Fact]
    public async Task AmbiguousIdentityKeepsFailedExecutionAndInputsButRollsBackBusinessWrites()
    {
        using var s = await Prepare(); Assert.Equal(SourceCollectionJobStatus.Succeeded, (await Process(s)).Status);
        var candidates = new[] { new CollectionCandidate("new-rollback", "Provisional", "https://example.invalid/new"),
            new CollectionCandidate("remote-demo-001", "Conflicting", Candidates()[1].SourceUrl), new CollectionCandidate("not-processed", "Untouched") };
        var conflicting = await Dispatch(s, items: candidates);
        var result = await Process(conflicting); Assert.Equal("AmbiguousIdentity", result.Code);
        await Terminal(conflicting, "failed", "AmbiguousIdentity", true);
        await using var db = Db();
        Assert.Equal(2, await db.Opportunities.CountAsync(x => x.WorkspaceId == s.Workspace));
        Assert.Equal(2, await db.OpportunitySources.CountAsync(x => x.WorkspaceId == s.Workspace));
        var failed = await db.SourceExecutions.Include(x => x.Items).ThenInclude(x => x.Sources)
            .SingleAsync(x => x.WorkspaceId == s.Workspace && x.StatusCode == "failed");
        Assert.Equal((3, 0, 0, 0, 1, 1, 1), (failed.ItemsFound, failed.ItemsCreated, failed.ItemsUpdated, failed.ItemsIgnored,
            failed.ItemsRolledBack, failed.ItemsRejected, failed.ItemsNotProcessed));
        Assert.Equal(new[] { "rolled-back", "rejected", "not-processed" }, failed.Items.OrderBy(x => x.ItemIndex).Select(x => x.OutcomeCode));
        Assert.All(failed.Items, x => { Assert.Null(x.OpportunityId); Assert.Null(x.OpportunityIdSnapshot); Assert.Empty(x.Sources); });
        Assert.Equal(SourceCollectionJobStatus.Conflict, (await Process(conflicting)).Status);
        Assert.Equal(2, await db.SourceExecutions.CountAsync(x => x.WorkspaceId == s.Workspace));
    }

    private sealed class CommitGate : DbTransactionInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken token = default)
        {
            Reached.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), token); return result;
        }
    }
    private async Task AssertJobLocked(Setup s)
    {
        await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"SourceCollectionJobs\" WHERE \"Id\" = {s.Job} FOR UPDATE NOWAIT"));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
    }
    [Fact]
    public async Task ConcurrentProcessingAndCallbackReplayCommitOneIngestion()
    {
        using var s = await Prepare(); var gate = new CommitGate();
        var first = Process(s, gate); await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await AssertJobLocked(s);
            await Unprocessed(s); // Separate connection cannot see any of the uncommitted business writes.
            var second = Process(s); var replay = s.Client.SendAsync(s.Host.Signed(s.Body));
            gate.Release.TrySetResult();
            Assert.Equal(SourceCollectionJobStatus.Succeeded, (await first).Status);
            Assert.Equal(SourceCollectionJobStatus.Conflict, (await second).Status);
            Assert.Equal(HttpStatusCode.Accepted, (await replay).StatusCode);
        }
        finally { gate.Release.TrySetResult(); }
        await Terminal(s, "succeeded", null, true);
        await using var db = Db(); Assert.Equal(1, await db.SourceExecutions.CountAsync(x => x.WorkspaceId == s.Workspace));
        Assert.Equal(2, await db.Opportunities.CountAsync(x => x.WorkspaceId == s.Workspace));
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ProcessingAndCancellationHaveExactlyOneAtomicWinner(bool processWins)
    {
        using var s = await Prepare(); var gate = new CommitGate();
        await using var firstDb = Db(gate); await using var secondDb = Db();
        var first = processWins ? Processor(s, firstDb).ProcessAsync(s.Job, default) : Jobs(s, firstDb).CancelAsync(s.Job, default);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await AssertJobLocked(s);
            var second = processWins ? Jobs(s, secondDb).CancelAsync(s.Job, default) : Processor(s, secondDb).ProcessAsync(s.Job, default);
            gate.Release.TrySetResult();
            Assert.Equal(SourceCollectionJobStatus.Succeeded, (await first.WaitAsync(TimeSpan.FromSeconds(20))).Status);
            Assert.Equal(SourceCollectionJobStatus.Conflict, (await second.WaitAsync(TimeSpan.FromSeconds(20))).Status);
        }
        finally { gate.Release.TrySetResult(); }
        if (processWins) await Terminal(s, "succeeded", null, true);
        else await Unprocessed(s, "cancelled");
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ConfigurationMutationAndProcessingRespectResourceLocks(bool processWins)
    {
        using var s = await Prepare(); var gate = new CommitGate();
        async Task Change(bool pause)
        {
            await using var db = pause ? Db(gate) : Db(); await using var tx = await db.Database.BeginTransactionAsync();
            await db.SourceConfigurations.Where(x => x.Id == s.Source).ExecuteUpdateAsync(x => x.SetProperty(p => p.Name, "Changed after receipt"));
            await tx.CommitAsync();
        }
        Task<SourceCollectionJobResult<bool>> processing; Task changing;
        if (processWins) { processing = Process(s, gate); await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20)); changing = Change(false); }
        else { changing = Change(true); await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20)); processing = Process(s); }
        try
        {
            // The losing writer cannot acquire the same resource row before the winner commits.
            await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"SourceConfigurations\" WHERE \"Id\" = {s.Source} FOR UPDATE NOWAIT"));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            gate.Release.TrySetResult();
            Assert.Equal(processWins ? SourceCollectionJobStatus.Succeeded : SourceCollectionJobStatus.Conflict,
                (await processing.WaitAsync(TimeSpan.FromSeconds(20))).Status);
            await changing.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { gate.Release.TrySetResult(); }
        await Terminal(s, processWins ? "succeeded" : "failed", processWins ? null : "RemoteContextChanged", processWins);
    }

    private sealed class CommitFault(bool after) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken token = default) =>
            !after ? throw new IOException("synthetic pre-commit interruption") : ValueTask.FromResult(result);
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken token = default) =>
            after ? throw new IOException("synthetic lost response after commit") : Task.CompletedTask;
    }
    private sealed class BusinessFault : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken token = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Opportunity>().Any())
                throw new IOException("synthetic interruption after business writes");
            return ValueTask.FromResult(result);
        }
    }
    [Theory]
    [InlineData("before-start")][InlineData("business")][InlineData("before-commit")][InlineData("after-commit")]
    public async Task InterruptedProcessingOrLostResponseSurvivesFreshScopesWithoutDuplicateIngestion(string boundary)
    {
        using var s = await Prepare();
        IInterceptor fault = boundary == "business" ? new BusinessFault() : new CommitFault(boundary == "after-commit");
        if (boundary != "before-start") await Assert.ThrowsAsync<IOException>(() => Process(s, fault));
        if (boundary != "after-commit") await Unprocessed(s);
        else await Terminal(s, "succeeded", null, true);
        // Fresh contexts and service graph, with the original durable logical connection.
        using var restarted = new SecurityTestHost(database.Postgres.GetConnectionString(), s.Host.Operator.UserId, s.Workspace);
        restarted.Operator.PasswordHash = s.Host.Operator.PasswordHash;
        restarted.Integration.ConnectionId = s.Host.Integration.ConnectionId;
        restarted.Integration.OutgoingKey = s.Host.Integration.OutgoingKey; restarted.Integration.IncomingKeys = s.Host.Integration.IncomingKeys;
        using var client = restarted.Client(); await SecurityTestHost.LoginAsync(client);
        Assert.Equal(boundary == "after-commit" ? HttpStatusCode.Conflict : HttpStatusCode.NoContent,
            (await client.PostAsync(ProcessUrl(s.Job), null)).StatusCode);
        await Terminal(s, "succeeded", null, true);
        await using var db = Db(); Assert.Equal(1, await db.SourceExecutions.CountAsync(x => x.WorkspaceId == s.Workspace));
        Assert.Equal(2, await db.Opportunities.CountAsync(x => x.WorkspaceId == s.Workspace));
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(RemoteCollectionMigrationTests.Current, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task SuccessBeforeTransportCompletionSurvivesLateAckOrDetachedAttemptRecovery(bool lateAck)
    {
        using var s = await Prepare(finishTransport: false);
        Assert.Equal(SourceCollectionJobStatus.Succeeded, (await Process(s)).Status);
        await using var db = Db();
        var op = (await db.RemoteCollectionIntents.SingleAsync(x => x.JobId == s.Job)).OperationId;
        var transport = await db.IntegrationAttempts.AsNoTracking().SingleAsync(x => x.OperationId == op);
        Assert.Equal("started", transport.StatusCode);
        if (lateAck) await new IntegrationStore(db, TimeProvider.System).FinishAttemptAsync(s.Workspace, op, transport.Id, IntegrationTransportOutcome.Acknowledged);
        else
        {
            await using var scope = s.Host.Services.CreateAsyncScope();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ISourceCollectionJobRecovery>().RecoverAsync(default));
        }
        Assert.Equal(lateAck ? "acknowledged" : "unknown", (await db.IntegrationAttempts.AsNoTracking().SingleAsync(x => x.Id == transport.Id)).StatusCode);
        await Terminal(s, "succeeded", null, true);
        Assert.Equal(1, await db.SourceExecutions.CountAsync(x => x.WorkspaceId == s.Workspace));
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.SendAsync(s.Host.Signed(s.Body))).StatusCode);
    }

    [Fact]
    public async Task RequestCancellationBeforeCommitRollsBackEverythingAndAllowsExplicitRetry()
    {
        using var s = await Prepare(); var gate = new CommitGate();
        await using (var db = Db(gate))
        {
            using var cancellation = new CancellationTokenSource();
            var processing = Processor(s, db).ProcessAsync(s.Job, cancellation.Token);
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        }
        await Unprocessed(s);
        Assert.Equal(SourceCollectionJobStatus.Succeeded, (await Process(s)).Status);
        await Terminal(s, "succeeded", null, true);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task RemotePreconditionRequiresOuterTransactionAndExactDurableBinding(bool transactionPresent)
    {
        using var s = await Prepare(); await using var db = Db();
        var job = await db.SourceCollectionJobs.AsNoTracking().SingleAsync(x => x.Id == s.Job);
        var intent = await db.RemoteCollectionIntents.SingleAsync(x => x.JobId == s.Job);
        var receipt = await db.IntegrationReceipts.SingleAsync(x => x.OperationId == intent.OperationId);
        var precondition = new RemoteCollectionPrecondition(s.Workspace, intent.Fingerprint, job.StartedAt!.Value,
            transactionPresent ? Guid.NewGuid() : job.Id, job.AttemptCount, intent.OperationId, receipt.MessageId, Guid.NewGuid(), job.TriggerTypeCode);
        await using var transaction = transactionPresent ? await db.Database.BeginTransactionAsync() : null;
        var request = new ProspectionCrm.Api.Dtos.Ingestions.IngestionRequest { PipelineStageId = s.Stage, Items = Candidates().Select(x =>
            new ProspectionCrm.Api.Dtos.Ingestions.IngestionItemRequest { ExternalId = x.ExternalId, Title = x.Title }).ToList() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new IngestionService(db, new FixedWorkspace(s.Workspace),
            NullLogger<IngestionService>.Instance).IngestAsync(s.Search, request, precondition, default));
        await Unprocessed(s);
    }

    [Fact]
    public async Task ExistingForeignKeyPreventsMovingDestinationStageBehindDurableJob()
    {
        using var s = await Prepare(); await using var db = Db();
        var pipeline = new Pipeline { WorkspaceId = s.Workspace, Name = "Different destination", TypeCode = "custom" };
        db.Add(pipeline); await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.PipelineStages.Where(x => x.Id == s.Stage)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.PipelineId, pipeline.Id)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.Equal(SourceCollectionJobStatus.Succeeded, (await Process(s)).Status);
        Assert.Equal(2, await db.Opportunities.CountAsync(x => x.WorkspaceId == s.Workspace && x.PipelineStageId == s.Stage));
    }
}
