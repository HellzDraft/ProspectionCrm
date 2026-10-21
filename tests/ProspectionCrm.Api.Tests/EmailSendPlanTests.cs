using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using ProspectionCrm.Api.Dtos.AutomationActionRequests;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Services.Email;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailSendPlanTests(AutomationJobDatabase database) : EmailSendPlanFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task DraftReadUpdateRevisionCanonicalRecipientsAndExplicitAddressRefresh()
    {
        using var s = await Prepare(); var p = await Create(s);
        Assert.Equal("draft", p.StatusCode); Assert.Equal(0, p.Revision);
        Assert.Equal(["a@example.invalid", "z@example.invalid"], p.Content.ToAddresses);
        Assert.Equal(["c@example.invalid"], p.Content.CcAddresses);
        Assert.Equal(s.Account.EmailAddress, p.Content.FromAddress);
        Assert.Equal(p.ContentHash, EmailSendSnapshots.Hash(p.Content));
        var list = await s.Client.GetFromJsonAsync<EmailSendPlanDto[]>("/api/email-send-plans"); Assert.Single(list!);
        var account = await s.Client.PutAsJsonAsync($"/api/email-accounts/{s.Account.Id}", new UpdateEmailAccountRequest("changed@example.invalid", null, "active", false, 0));
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/email-send-plans/{p.Id}/submit", new { revision = 0 })).StatusCode);
        var update = await s.Client.PutAsJsonAsync($"/api/email-send-plans/{p.Id}", new UpdateEmailSendPlanRequest(0, Content(s) with { Subject = "Updated" }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode); p = (await update.Content.ReadFromJsonAsync<EmailSendPlanDto>())!;
        Assert.Equal(1, p.Revision); Assert.Equal("changed@example.invalid", p.Content.FromAddress);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PutAsJsonAsync($"/api/email-send-plans/{p.Id}", new UpdateEmailSendPlanRequest(0, Content(s)))).StatusCode);
        await Submit(s, p); await AssertNoEffects(s);
    }
    [Fact]
    public async Task ApproveFreezesExactSnapshotAndNeverCreatesASendOrMessage()
    {
        using var s = await Prepare(); var draft = await Create(s); var p = await Submit(s, draft);
        Assert.Equal("pending-approval", p.StatusCode); Assert.Equal(draft.ContentHash, p.ContentHash);
        var request = await s.Client.GetFromJsonAsync<AutomationActionRequestDto>($"/api/automation-action-requests/{p.AutomationActionRequestId}");
        Assert.Equal("approval-required", request!.DecisionRequirementCode); Assert.False(request.IsStale);
        Assert.Null(request.AutomationJobId); Assert.Null(request.AutomationRuleId); Assert.Null(request.ActionPlan);
        Assert.Equal(p.ContentHash, request.ContentFingerprint); Assert.Equal(p.ContentHash, EmailSendSnapshots.Hash(request.EmailPlan!));
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, p)).StatusCode);
        var approved = await Read(s, p.Id); Assert.Equal("approved", approved.StatusCode); Assert.NotNull(approved.ApprovedAt);
        Assert.Equal(draft.ContentHash, approved.ContentHash); Assert.Equal(p.ContentHash, EmailSendSnapshots.Hash(approved.Content));
        var repeat = await Decide(s, p); Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(JsonSerializer.Serialize(approved), JsonSerializer.Serialize(await Read(s, p.Id)));
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(s, p, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PutAsJsonAsync($"/api/email-send-plans/{p.Id}", new UpdateEmailSendPlanRequest(approved.Revision, Content(s)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/email-send-plans/{p.Id}/cancel", new { revision = approved.Revision })).StatusCode);
        await AssertNoEffects(s);
    }
    [Fact]
    public async Task RejectAndRepeatAreTerminalWithoutEffects()
    {
        using var s = await Prepare(); var p = await Submit(s, await Create(s));
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, p, false)).StatusCode);
        var rejected = await Read(s, p.Id); Assert.Equal("rejected", rejected.StatusCode); Assert.NotNull(rejected.RejectedAt);
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, p, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(s, p)).StatusCode);
        Assert.Equal(JsonSerializer.Serialize(rejected), JsonSerializer.Serialize(await Read(s, p.Id))); await AssertNoEffects(s);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CancelDraftOrPendingInvalidatesAnyDecision(bool pending)
    {
        using var s = await Prepare(); var p = await Create(s); if (pending) p = await Submit(s, p);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsJsonAsync($"/api/email-send-plans/{p.Id}/cancel", new { revision = p.Revision })).StatusCode);
        var cancelled = await Read(s, p.Id); Assert.Equal("cancelled", cancelled.StatusCode); Assert.NotNull(cancelled.CancelledAt);
        if (pending) Assert.Equal(HttpStatusCode.Conflict, (await Decide(s, p)).StatusCode);
        await AssertNoEffects(s);
    }
    [Fact]
    public async Task DuplicateSubmitAndStaleRevisionDoNotCreateAnotherRequest()
    {
        using var s = await Prepare(); var draft = await Create(s); var pending = await Submit(s, draft);
        foreach (var revision in new[] { draft.Revision, pending.Revision })
            Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/email-send-plans/{draft.Id}/submit", new { revision })).StatusCode);
        await using var db = Db(); Assert.Single(await db.AutomationActionRequests.Where(x => x.WorkspaceId == s.Workspace).ToListAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PutAsJsonAsync($"/api/email-send-plans/{draft.Id}", new UpdateEmailSendPlanRequest(pending.Revision, Content(s)))).StatusCode);
    }
    [Theory]
    [InlineData("disabled", false)][InlineData("revoked", false)][InlineData("archived", false)][InlineData("address", false)]
    [InlineData("disabled", true)][InlineData("revoked", true)][InlineData("archived", true)][InlineData("address", true)]
    [InlineData("connection", true)]
    public async Task ChangedAccountRefusesSubmitOrApproveWithoutPartialDecision(string change, bool pending)
    {
        using var s = await Prepare(); var p = await Create(s); if (pending) p = await Submit(s, p);
        if (change == "connection") s.Host.Services.GetRequiredService<IOptionsMonitor<ProspectionCrm.Api.Services.Integrations.IntegrationOptions>>().CurrentValue.ConnectionId = Guid.NewGuid();
        else
        {
            var response = await s.Client.PutAsJsonAsync($"/api/email-accounts/{s.Account.Id}", new UpdateEmailAccountRequest(
                change == "address" ? "other@example.invalid" : s.Account.EmailAddress, null,
                change is "disabled" or "revoked" ? change : "active", change == "archived", 0));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var decision = pending ? await Decide(s, p) : await s.Client.PostAsJsonAsync($"/api/email-send-plans/{p.Id}/submit", new { revision = p.Revision });
        Assert.Equal(HttpStatusCode.Conflict, decision.StatusCode); Assert.Equal(p.StatusCode, (await Read(s, p.Id)).StatusCode);
        if (pending) { await using var db = Db(); Assert.Equal("pending", (await db.AutomationActionRequests.SingleAsync(x => x.Id == p.AutomationActionRequestId)).StatusCode); }
        await AssertNoEffects(s);
    }
    [Fact]
    public async Task KillSwitchBlocksPreparationButNotAnAlreadyPendingHumanDecision()
    {
        using var s = await Prepare(); var pending = await Submit(s, await Create(s)); var draft = await Create(s);
        await using var db = Db(); var settings = await db.AutomationRuntimeSettings.SingleAsync(x => x.WorkspaceId == s.Workspace);
        settings.IsEnabled = false; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/email-send-plans/{draft.Id}/submit", new { revision = draft.Revision })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, pending)).StatusCode); await AssertNoEffects(s);
    }
    [Fact]
    public async Task FingerprintRevalidationAndDatabaseSnapshotImmutability()
    {
        using var s = await Prepare(); var p = await Submit(s, await Create(s)); await using var db = Db();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "EmailSendPlans" SET "BodyText" = 'tampered' WHERE "Id" = {p.Id}
            """)); Assert.Equal("23514", error.SqlState);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AutomationActionRequests" SET "ActionPlanJson" = jsonb_set("ActionPlanJson", ARRAY['bodyText'], '"tampered"')
            WHERE "Id" = {p.AutomationActionRequestId}
            """);
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(s, p)).StatusCode);
        var request = await s.Client.GetFromJsonAsync<AutomationActionRequestDto>($"/api/automation-action-requests/{p.AutomationActionRequestId}");
        Assert.True(request!.IsStale); Assert.Null(request.EmailPlan); Assert.Equal("pending", request.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Decide(s, p, false)).StatusCode);
    }
}
