using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Email;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public abstract class EmailSyncFixture(AutomationJobDatabase database)
{
    protected AutomationJobDatabase Database => database;
    protected sealed class Scenario(SecurityTestHost host, HttpClient client, EmailAccountDto account, EmailSyncJobDto job) : IDisposable
    {
        public SecurityTestHost Host { get; } = host;
        public HttpClient Client { get; } = client;
        public EmailAccountDto Account { get; set; } = account;
        public EmailSyncJobDto Job { get; set; } = job;
        public Guid Workspace => Host.Operator.WorkspaceId;
        public byte[] Body { get; set; } = [];
        public void Dispose() { Client.Dispose(); Host.Dispose(); }
    }
    protected sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    protected sealed class SimulatedTransport(Func<EmailSyncCommand, CancellationToken, Task<IntegrationTransportOutcome>> send) : IEmailSyncTransport
    {
        public bool IsAvailable => true;
        public Task<IntegrationTransportOutcome> DispatchAsync(EmailSyncCommand command, CancellationToken token) => send(command, token);
    }
    protected ProspectionCrmDbContext Db(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
        .UseNpgsql(database.Postgres.GetConnectionString()).AddInterceptors(interceptors).Options);
    protected static EmailSyncService Sync(Scenario s, ProspectionCrmDbContext db) => new(db,
        new EmailAccountService(db, new Monitor<IntegrationOptions>(s.Host.Integration)), new(db));
    protected async Task<Scenario> Prepare(bool dispatch = true, bool receive = true, InboundEmail[]? messages = null)
    {
        var host = await SecurityTestHost.CreateAsync(database); var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var response = await client.PostAsJsonAsync("/api/email-accounts", new CreateEmailAccountRequest(host.Integration.ConnectionId,
            "synthetic", "account-1", "mailbox@example.invalid", "Synthetic mailbox"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = (await response.Content.ReadFromJsonAsync<EmailAccountDto>())!;
        response = await client.PostAsJsonAsync($"/api/email-accounts/{account.Id}/sync-jobs", new EnqueueEmailSyncRequest());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<EmailSyncJobDto>())!;
        var scenario = new Scenario(host, client, account, job);
        if (dispatch) await Dispatch(scenario);
        scenario.Body = await Body(scenario, messages ?? [EmailSyncContractTests.Message("a"), EmailSyncContractTests.Message("b", null)]);
        if (receive) Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(scenario.Body))).StatusCode);
        return scenario;
    }
    protected async Task Dispatch(Scenario s, Func<EmailSyncCommand, CancellationToken, Task<IntegrationTransportOutcome>>? send = null)
    {
        await using var db = Db();
        await new EmailSyncDispatcher(Sync(s, db), new SimulatedTransport(send ?? ((_, _) => Task.FromResult(IntegrationTransportOutcome.Acknowledged))))
            .DispatchAsync(s.Workspace, s.Job.Id);
    }
    protected async Task<byte[]> Body(Scenario s, IReadOnlyList<InboundEmail> messages, string outcome = "succeeded", string? cursor = "cursor-next")
    {
        await using var db = Db(); var op = await db.IntegrationOperations.SingleAsync(x => x.Id == s.Job.IntegrationOperationId);
        return Encoding.UTF8.GetBytes(EmailSyncContracts.Serialize(new EmailSyncResult(op.Version, Guid.NewGuid(), op.Id, op.CorrelationId,
            op.CommandMessageId, op.Type, new(s.Account.Id, outcome, cursor, messages))));
    }
    protected async Task Process(Scenario s, params IInterceptor[] interceptors)
    { await using var db = Db(interceptors); await Sync(s, db).ProcessAsync(s.Workspace, s.Job.Id, default); }
    protected async Task NewJob(Scenario s)
    {
        var response = await s.Client.PostAsJsonAsync($"/api/email-accounts/{s.Account.Id}/sync-jobs", new EnqueueEmailSyncRequest());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); s.Job = (await response.Content.ReadFromJsonAsync<EmailSyncJobDto>())!;
    }
    protected async Task AssertState(Scenario s, string status, int count, string? cursor, string? error = null)
    {
        await using var db = Db(); var job = await db.EmailSyncJobs.SingleAsync(x => x.Id == s.Job.Id);
        Assert.Equal(status, job.StatusCode); Assert.Equal(error, job.ErrorCode);
        Assert.Equal(count, await db.EmailMessages.CountAsync(x => x.EmailAccountId == s.Account.Id));
        Assert.Equal(cursor, (await db.EmailSyncStates.SingleAsync(x => x.EmailAccountId == s.Account.Id)).Cursor);
        var op = await db.IntegrationOperations.SingleAsync(x => x.Id == job.IntegrationOperationId);
        var receipt = await db.IntegrationReceipts.SingleOrDefaultAsync(x => x.OperationId == op.Id);
        if (status is "succeeded" or "failed" or "cancelled")
        {
            Assert.NotNull(job.FinishedAt); Assert.NotNull(op.CompletedAt); Assert.Equal(status == "cancelled" ? "failed" : status, op.StatusCode);
            if (receipt is not null) Assert.NotNull(receipt.ProcessedAt);
        }
        else { Assert.Null(job.FinishedAt); Assert.Null(op.CompletedAt); if (receipt is not null) Assert.Null(receipt.ProcessedAt); }
    }
}
