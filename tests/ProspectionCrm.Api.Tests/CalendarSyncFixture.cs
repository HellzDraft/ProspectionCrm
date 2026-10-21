using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Services.Calendar;
using ProspectionCrm.Api.Services.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public abstract class CalendarSyncFixture(AutomationJobDatabase database)
{
    protected AutomationJobDatabase Database => database;
    protected sealed class Scenario(SecurityTestHost host, HttpClient client, CalendarAccountDto account, CalendarDto calendar, CalendarSyncJobDto job) : IDisposable
    {
        public SecurityTestHost Host { get; } = host;
        public HttpClient Client { get; } = client;
        public CalendarAccountDto Account { get; set; } = account;
        public CalendarDto Calendar { get; set; } = calendar;
        public CalendarSyncJobDto Job { get; set; } = job;
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
    protected sealed class SimulatedTransport(Func<CalendarSyncCommand, CancellationToken, Task<IntegrationTransportOutcome>> send) : ICalendarSyncTransport
    {
        public bool IsAvailable => true;
        public Task<IntegrationTransportOutcome> DispatchAsync(CalendarSyncCommand command, CancellationToken token) => send(command, token);
    }
    protected ProspectionCrmDbContext Db(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<ProspectionCrmDbContext>()
        .UseNpgsql(database.Postgres.GetConnectionString()).AddInterceptors(interceptors).Options);
    protected static CalendarSyncService Sync(Scenario s, ProspectionCrmDbContext db) => new(db,
        new CalendarAccountService(db, new Monitor<IntegrationOptions>(s.Host.Integration)), new(db));
    protected async Task<Scenario> Prepare(bool dispatch = true, bool receive = true, InboundCalendarEvent[]? messages = null)
    {
        var host = await SecurityTestHost.CreateAsync(database); var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        var response = await client.PostAsJsonAsync("/api/calendar-accounts", new CreateCalendarAccountRequest(host.Integration.ConnectionId,
            "synthetic", "account-1", "Synthetic account"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = (await response.Content.ReadFromJsonAsync<CalendarAccountDto>())!;
        response = await client.PostAsJsonAsync($"/api/calendar-accounts/{account.Id}/calendars", new CreateCalendarRequest("calendar-1", "Synthetic calendar", "Europe/Paris"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var calendar = (await response.Content.ReadFromJsonAsync<CalendarDto>())!;
        response = await client.PostAsJsonAsync($"/api/calendars/{calendar.Id}/sync-jobs", new EnqueueCalendarSyncRequest());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<CalendarSyncJobDto>())!;
        var scenario = new Scenario(host, client, account, calendar, job);
        if (dispatch) await Dispatch(scenario);
        scenario.Body = await Body(scenario, messages ?? [CalendarSyncContractTests.Event("a"), CalendarSyncContractTests.Event("b")]);
        if (receive) Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(host.Signed(scenario.Body))).StatusCode);
        return scenario;
    }
    protected async Task Dispatch(Scenario s, Func<CalendarSyncCommand, CancellationToken, Task<IntegrationTransportOutcome>>? send = null)
    {
        await using var db = Db();
        await new CalendarSyncDispatcher(Sync(s, db), new SimulatedTransport(send ?? ((_, _) => Task.FromResult(IntegrationTransportOutcome.Acknowledged))))
            .DispatchAsync(s.Workspace, s.Job.Id);
    }
    protected async Task<byte[]> Body(Scenario s, IReadOnlyList<InboundCalendarEvent> messages, string outcome = "succeeded", string? cursor = "cursor-next")
    {
        await using var db = Db(); var op = await db.IntegrationOperations.SingleAsync(x => x.Id == s.Job.IntegrationOperationId);
        return Encoding.UTF8.GetBytes(CalendarSyncContracts.Serialize(new CalendarSyncResult(op.Version, Guid.NewGuid(), op.Id, op.CorrelationId,
            op.CommandMessageId, op.Type, new(s.Calendar.Id, s.Job.Id, outcome, cursor, messages))));
    }
    protected async Task Process(Scenario s, params IInterceptor[] interceptors)
    { await using var db = Db(interceptors); await Sync(s, db).ProcessAsync(s.Workspace, s.Job.Id, default); }
    protected async Task NewJob(Scenario s)
    {
        var response = await s.Client.PostAsJsonAsync($"/api/calendars/{s.Calendar.Id}/sync-jobs", new EnqueueCalendarSyncRequest());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); s.Job = (await response.Content.ReadFromJsonAsync<CalendarSyncJobDto>())!;
    }
    protected async Task AssertState(Scenario s, string status, int count, string? cursor, string? error = null)
    {
        await using var db = Db(); var job = await db.CalendarSyncJobs.SingleAsync(x => x.Id == s.Job.Id);
        Assert.Equal(status, job.StatusCode); Assert.Equal(error, job.ErrorCode);
        Assert.Equal(count, await db.CalendarEvents.CountAsync(x => x.CalendarId == s.Calendar.Id));
        Assert.Equal(cursor, (await db.CalendarSyncStates.SingleAsync(x => x.CalendarId == s.Calendar.Id)).Cursor);
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
