using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Calendar;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public abstract class CalendarEventDeliveryFixture(AutomationJobDatabase database) : CalendarEventPlanFixture(database)
{
    protected sealed class Provider : ICalendarEventTransport
    {
        public InMemoryCalendarEventTransport Simulator { get; } = new();
        public bool IsAvailable => true;
        public int Calls;
        public string Mode = "ack";
        public CalendarEventCommand? Command;
        public Func<Task>? BeforeEffect, AfterEffect;
        public async Task<CalendarEventTransportOutcome> DispatchAsync(CalendarEventCommand command, CancellationToken token)
        {
            Interlocked.Increment(ref Calls); Command = command;
            if (BeforeEffect is not null) await BeforeEffect();
            if (Mode == "crash-before") throw new IOException();
            await Simulator.DispatchAsync(command, token);
            if (AfterEffect is not null) await AfterEffect();
            if (Mode == "timeout") throw new TaskCanceledException();
            if (Mode == "disconnect") throw new IOException();
            return Mode == "http-error" ? CalendarEventTransportOutcome.Unknown : CalendarEventTransportOutcome.Acknowledged;
        }
        public CalendarEventResult Proof => Simulator.FindProof(Command!.IdempotencyKey)!;
    }
    protected static string Route(Guid id, string verb = "") => $"/api/calendar-event-deliveries/{id}" + (verb.Length == 0 ? "" : "/" + verb);
    protected static async Task<CalendarEventPlanDto> Approved(Scenario s, CalendarEventContent? content = null)
    {
        var p = await Submit(s, await Create(s, content));
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, p)).StatusCode); return await Read(s, p.Id);
    }
    protected static async Task<CalendarEventDelivery> Delivery(Scenario s, CalendarEventPlanDto p)
    {
        var response = await s.Client.PostAsync($"/api/calendar-event-deliveries/from-plan/{p.Id}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<CalendarEventDelivery>())!;
    }
    protected static async Task<CalendarEventDelivery> ReadDelivery(Scenario s, Guid id) => (await s.Client.GetFromJsonAsync<CalendarEventDelivery>(Route(id)))!;
    protected static Task<HttpResponseMessage> Receive(Scenario s, CalendarEventResult proof, string key = "in-1") =>
        s.Client.SendAsync(s.Host.Signed(Encoding.UTF8.GetBytes(CalendarEventContracts.Serialize(proof)), key));
    protected static async Task Dispatch(Scenario s, Guid id) => Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsync(Route(id, "dispatch"), null)).StatusCode);
    protected static async Task Reconcile(Scenario s, Guid id) => Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsync(Route(id, "reconcile"), null)).StatusCode);
    protected static async Task Prove(Scenario s, Guid id, CalendarEventResult proof)
    { Assert.Equal(HttpStatusCode.Accepted, (await Receive(s, proof)).StatusCode); await Reconcile(s, id); }
    protected async Task<Scenario> Restart(Scenario s, Provider provider, bool retainConnection = true)
    {
        var host = await SecurityTestHost.CreateAsync(Database, s.Workspace);
        if (retainConnection)
        {
            host.Integration.ConnectionId = s.Host.Integration.ConnectionId;
            host.Integration.IncomingKeys = s.Host.Integration.IncomingKeys;
            host.Integration.OutgoingKey = s.Host.Integration.OutgoingKey;
        }
        var network = new NoNetwork(); host.TransportHandler = () => network; host.CalendarEventTransport = provider;
        var client = host.Client(); await SecurityTestHost.LoginAsync(client);
        return new(host, client, s.Account, s.Calendar, network);
    }
    protected async Task<CalendarEvent> Target(Scenario s, Provider provider, bool occurrence = false)
    {
        var value = new InboundCalendarEvent("simulated-existing-" + Guid.NewGuid().ToString("N"), 4, "Before", "Original", "Desk",
            DateTimeOffset.Parse("2026-10-21T08:00:00Z"), DateTimeOffset.Parse("2026-10-21T09:00:00Z"), false, "Europe/Paris", "confirmed",
            occurrence ? "series" : null, occurrence ? "occurrence" : null);
        await Import(s, value); provider.Simulator.Seed(s.Account.ConnectionId, s.Calendar.Id, s.Calendar.ExternalCalendarId, "synthetic", value);
        var refs = await References(s);
        await using var db = Db();
        await db.CalendarEvents.Where(x => x.CalendarId == s.Calendar.Id).ExecuteUpdateAsync(x => x
            .SetProperty(e => e.OpportunityId, refs.Opportunity).SetProperty(e => e.ContactId, refs.Contact));
        return await db.CalendarEvents.AsNoTracking().SingleAsync(x => x.CalendarId == s.Calendar.Id);
    }
    protected async Task<(Guid Opportunity, Guid Contact)> References(Scenario s)
    {
        await using var db = Db();
        var contact = new Contact { WorkspaceId = s.Workspace, FirstName = "Synthetic", LastName = "Reference" };
        var pipeline = new Pipeline { WorkspaceId = s.Workspace, Name = "Calendar", TypeCode = "custom" };
        var stage = new PipelineStage { Pipeline = pipeline, Name = "Active", CategoryCode = "active" };
        var opportunity = new Opportunity { WorkspaceId = s.Workspace, PipelineStage = stage, Title = "Explicit reference", PriorityCode = "normal" };
        db.AddRange(contact, opportunity); await db.SaveChangesAsync(); return (opportunity.Id, contact.Id);
    }
    protected async Task Import(Scenario s, InboundCalendarEvent value)
    {
        await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
        await CalendarSyncLocks.WorkspaceAsync(db, s.Workspace, default);
        var (_, calendar) = await CalendarSyncLocks.CalendarAsync(db, s.Workspace, s.Calendar.Id, default);
        Assert.Null((await new InboundCalendarImportService(db).ImportAsync(calendar, [value], default)).ConflictId); await tx.CommitAsync();
    }
    protected static CalendarEventContent UpdateContent(Scenario s, CalendarEvent e) => Content(s) with { OperationCode = "update", TargetCalendarEventId = e.Id };
    protected async Task<(CalendarEventCommand Command, Guid Attempt)> BeginWithoutTransport(Scenario s, CalendarEventDelivery d)
    {
        await using var db = Db();
        // Invoke the same production pre-dispatch transaction with a human actor, then simulate a process loss.
        var service = new CalendarEventDeliveryService(db,
            new CalendarEventPlanService(db, new CalendarAccountService(db, new Monitor<ProspectionCrm.Api.Services.Integrations.IntegrationOptions>(s.Host.Integration)),
                new ProspectionCrm.Api.Services.Automation.AutomationSafetyPolicy(), new Actor(s)),
            new ProspectionCrm.Api.Services.Automation.AutomationSafetyPolicy(), new Actor(s),
            new Monitor<ProspectionCrm.Api.Services.Integrations.IntegrationOptions>(s.Host.Integration),
            new Monitor<ProspectionCrm.Api.Security.OperatorOptions>(s.Host.Operator), new InboundCalendarImportService(db), s.Host.Clock);
        return await service.BeginAsync(s.Workspace, d.Id, default);
    }
    private sealed class Monitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    { public T CurrentValue => value; public T Get(string? name) => value; public IDisposable? OnChange(Action<T, string?> listener) => null; }
    private sealed class Actor(Scenario s) : ProspectionCrm.Api.Security.IHumanActor
    {
        public Task<(Guid UserId, Guid WorkspaceId)> RequireAsync(CancellationToken token = default) =>
            Task.FromResult((s.Host.Operator.UserId, s.Workspace));
    }
}
