using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Security;
using ProspectionCrm.Api.Services.Integrations;
using ProspectionCrm.Api.Services.Email;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Dtos.Integrations;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class IntegrationDispatchBudgetTests(AutomationJobDatabase database)
    : CalendarEventDeliveryFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData(0, 1000, false)] [InlineData(-1, 1000, false)]
    [InlineData(10001, 1000, false)] [InlineData(60, 0, false)]
    [InlineData(60, 1000001, false)] [InlineData(1, 1, true)]
    [InlineData(10000, 1000000, true)]
    public void ConfigurationBoundsApplyEvenWhenDisabled(int minute, int day, bool valid)
    {
        var options = new IntegrationOptions { DispatchLimits = new() { MaxStartsPerMinute = minute, MaxStartsPerDay = day } };
        Assert.False(options.DispatchLimits.Enabled);
        Assert.Equal(valid, options.IsValid(new OperatorOptions()));
    }

    private async Task Seed(Guid workspace, Guid connection, string type, DateTimeOffset at, string status = "unknown")
    {
        await using var db = Db();
        var op = new IntegrationOperation { Id = Guid.NewGuid(), WorkspaceId = workspace, ConnectionId = connection,
            CommandMessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), Version = 1, Type = type,
            StatusCode = "awaiting-result", CreatedAt = at.AddDays(-1) };
        db.Add(op);
        db.Add(new IntegrationAttempt { Id = Guid.NewGuid(), OperationId = op.Id, Number = 1, StatusCode = status,
            StartedAt = at, FinishedAt = status == "started" ? null : at });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ExactUtcAndRollingBoundariesIgnoreSessionTimezoneAndScope()
    {
        using var s = await Prepare(); using var foreign = await Prepare();
        var connection = s.Host.Integration.ConnectionId;
        var at = DateTimeOffset.Parse("2026-10-23T00:00:30Z");
        await Seed(s.Workspace, connection, "email.send", at.AddSeconds(-60)); // excluded minute, previous day
        await Seed(s.Workspace, connection, "calendar.event.create", at.AddSeconds(-59)); // minute, previous day
        await Seed(s.Workspace, connection, "calendar.event.update", at.AddSeconds(-30), "failed"); // midnight included
        await Seed(s.Workspace, connection, "email.send", at, "started"); // included upper bound
        await Seed(s.Workspace, connection, "email.sync", at);
        await Seed(s.Workspace, Guid.NewGuid(), "email.send", at);
        await Seed(foreign.Workspace, connection, "email.send", at);
        await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL TIME ZONE 'Pacific/Honolulu'");
        var usage = await IntegrationDispatchBudget.ReadUsageAsync(db, s.Workspace, connection, at, default);
        Assert.Equal(3, usage.Minute); Assert.Equal(2, usage.Day);
        usage = await IntegrationDispatchBudget.ReadUsageAsync(db, s.Workspace, connection, at.AddDays(1), default);
        Assert.Equal(0, usage.Minute); Assert.Equal(0, usage.Day);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task RefusalPreservesPendingApprovalAndAllowsLaterHumanStart(bool update, bool day)
    {
        var provider = new Provider(); using var s = await Prepare(provider, h =>
            h.Integration.DispatchLimits = new() { Enabled = true, MaxStartsPerMinute = 1, MaxStartsPerDay = day ? 1 : 1000 });
        var plan = await Approved(s, update ? UpdateContent(s, await Target(s, provider)) : null);
        var d = await Delivery(s, plan);
        await using var db = Db(); var now = await IntegrationSyncLocks.Now(db, default);
        // Day test seeds midnight (outside rolling minute except during the first minute UTC).
        var seedAt = day ? new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero) : now;
        await Seed(s.Workspace, s.Host.Integration.ConnectionId, "email.send", seedAt);
        var before = await Snapshot(s);
        var response = await s.Client.PostAsync(Route(d.Id, "dispatch"), null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(day && now - seedAt >= TimeSpan.FromMinutes(1) ? "IntegrationDispatchDayLimitReached" : "IntegrationDispatchMinuteLimitReached",
            await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await Snapshot(s)); Assert.Equal(0, provider.Calls);
        // Age only synthetic test history; no waiting and no provider invocation.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "IntegrationAttempts" SET "StartedAt"="StartedAt"-interval '2 days', "FinishedAt"="FinishedAt"-interval '2 days'
            WHERE "OperationId" IN (SELECT "Id" FROM "IntegrationOperations" WHERE "WorkspaceId"={s.Workspace})
            """);
        await Dispatch(s, d.Id); Assert.Equal(1, provider.Calls);
        Assert.Equal("approved", (await Read(s, plan.Id)).StatusCode);
    }

    [Fact]
    public async Task DisabledOptionDoesNotLimitAndUnknownConsumesEnabledBudget()
    {
        var provider = new Provider { Mode = "timeout" }; using var s = await Prepare(provider, h =>
            h.Integration.DispatchLimits = new() { Enabled = false, MaxStartsPerMinute = 1, MaxStartsPerDay = 1 });
        var first = await Delivery(s, await Approved(s)); var second = await Delivery(s, await Approved(s));
        await Dispatch(s, first.Id); await Dispatch(s, second.Id); Assert.Equal(2, provider.Calls);
        Assert.Equal("operation-unknown", (await ReadDelivery(s, first.Id)).StatusCode);
        s.Host.Services.GetRequiredService<IOptionsMonitor<IntegrationOptions>>().CurrentValue.DispatchLimits.Enabled = true;
        var third = await Delivery(s, await Approved(s));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(Route(third.Id, "dispatch"), null)).StatusCode);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task TwoApiHostsWithIndependentGatesAdmitExactlyOneStart()
    {
        var provider = new Provider(); using var s = await Prepare(provider, h =>
            h.Integration.DispatchLimits = new() { Enabled = true, MaxStartsPerMinute = 1 });
        using var other = await Restart(s, provider);
        // Restart fixture copies identity/connection, not budget configuration. Set its effective test options.
        other.Host.Services.GetRequiredService<IOptionsMonitor<IntegrationOptions>>().CurrentValue.DispatchLimits =
            new() { Enabled = true, MaxStartsPerMinute = 1 };
        var first = await Delivery(s, await Approved(s)); var second = await Delivery(s, await Approved(s));
        // While transport runs, admission's transaction and runtime lock must already be released.
        var lockAcquiredDuringTransport = false;
        provider.BeforeEffect = async () =>
        {
            await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='2s'");
            await ProspectionCrm.Api.Services.Automation.AutomationRuntimeLock.AcquireAsync(db, s.Workspace, default);
            lockAcquiredDuringTransport = true;
        };
        var responses = await Task.WhenAll(s.Client.PostAsync(Route(first.Id, "dispatch"), null), other.Client.PostAsync(Route(second.Id, "dispatch"), null));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.NoContent);
        var refused = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("IntegrationDispatchMinuteLimitReached", await refused.Content.ReadAsStringAsync());
        Assert.Equal(1, provider.Calls);
        Assert.True(lockAcquiredDuringTransport);
        await using var check = Db();
        Assert.Equal(1, await check.IntegrationAttempts.CountAsync(x => check.IntegrationOperations.Any(o => o.Id == x.OperationId && o.WorkspaceId == s.Workspace)));
    }

    private async Task<string> Snapshot(Scenario s)
    {
        await using var db = Db();
        return JsonSerializer.Serialize(new {
            Operations = await db.IntegrationOperations.AsNoTracking().Where(x => x.WorkspaceId == s.Workspace).OrderBy(x => x.Id).ToListAsync(),
            Attempts = await db.IntegrationAttempts.AsNoTracking().Where(x => db.IntegrationOperations.Any(o => o.Id == x.OperationId && o.WorkspaceId == s.Workspace)).OrderBy(x => x.Id).ToListAsync(),
            Deliveries = await db.CalendarEventDeliveries.AsNoTracking().Where(x => x.WorkspaceId == s.Workspace).OrderBy(x => x.Id).ToListAsync(),
            Plans = await db.CalendarEventPlans.AsNoTracking().Where(x => x.WorkspaceId == s.Workspace).OrderBy(x => x.Id).ToListAsync(),
            EmailPlans = await db.EmailSendPlans.AsNoTracking().Where(x => x.WorkspaceId == s.Workspace).OrderBy(x => x.Id).ToListAsync(),
            Approvals = await db.AutomationActionRequests.AsNoTracking().Where(x => x.WorkspaceId == s.Workspace).OrderBy(x => x.Id).ToListAsync()
        });
    }

    private sealed class EmailTransport : IEmailSendTransport
    {
        public bool IsAvailable => true;
        public int Calls;
        public Task<EmailSendTransportOutcome> DispatchAsync(EmailSendCommand command, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Task.FromResult(EmailSendTransportOutcome.Unknown); }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task EmailAndCalendarShareBudgetWithoutMutatingRefusedIntent(bool emailFirst, bool concurrent)
    {
        var email = new EmailTransport(); var calendar = new Provider();
        using var s = await Prepare(calendar, h => {
            h.EmailSendTransport = email;
            h.Integration.DispatchLimits = new() { Enabled = true, MaxStartsPerMinute = 1 };
        });
        var account = await (await s.Client.PostAsJsonAsync("/api/email-accounts", new CreateEmailAccountRequest(
            s.Host.Integration.ConnectionId, "synthetic", "budget", "sender@example.invalid"))).Content.ReadFromJsonAsync<EmailAccountDto>();
        var plan = await (await s.Client.PostAsJsonAsync("/api/email-send-plans", new EmailSendContent(account!.Id,
            ["recipient@example.invalid"], [], "Budget", "Synthetic"))).Content.ReadFromJsonAsync<EmailSendPlanDto>();
        plan = await (await s.Client.PostAsJsonAsync($"/api/email-send-plans/{plan!.Id}/submit", new { revision = plan.Revision })).Content.ReadFromJsonAsync<EmailSendPlanDto>();
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsJsonAsync($"/api/automation-action-requests/{plan!.AutomationActionRequestId}/approve", new { })).StatusCode);
        var delivery = await (await s.Client.PostAsync($"/api/email-send-deliveries/from-plan/{plan.Id}", null)).Content.ReadFromJsonAsync<EmailSendDelivery>();
        var cd = await Delivery(s, await Approved(s));
        var emailRoute = $"/api/email-send-deliveries/{delivery!.Id}/dispatch";
        var calendarRoute = Route(cd.Id, "dispatch");
        if (concurrent)
        {
            var responses = await Task.WhenAll(s.Client.PostAsync(emailRoute, null), s.Client.PostAsync(calendarRoute, null));
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.NoContent);
            var refused = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            Assert.Contains("IntegrationDispatchMinuteLimitReached", await refused.Content.ReadAsStringAsync());
            Assert.Equal(1, email.Calls + calendar.Calls);
            return;
        }
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsync(emailFirst ? emailRoute : calendarRoute, null)).StatusCode);
        await using var db = Db();
        var before = JsonSerializer.Serialize(await db.EmailSendDeliveries.AsNoTracking().SingleAsync(x => x.Id == delivery.Id));
        var calendarBefore = await Snapshot(s);
        var response = await s.Client.PostAsync(emailFirst ? calendarRoute : emailRoute, null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("IntegrationDispatchMinuteLimitReached", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, JsonSerializer.Serialize(await db.EmailSendDeliveries.AsNoTracking().SingleAsync(x => x.Id == delivery.Id)));
        Assert.Equal(calendarBefore, await Snapshot(s));
        Assert.Equal(1, email.Calls + calendar.Calls);
    }
}
