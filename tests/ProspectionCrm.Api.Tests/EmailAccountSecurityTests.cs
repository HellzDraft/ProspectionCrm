using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Services.Email;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class EmailAccountSecurityTests(AutomationJobDatabase database) : EmailSyncFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Fact]
    public async Task AccountCreationIsUniqueAndUpdatesAreRevisionProtectedWithoutSecrets()
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var request = new CreateEmailAccountRequest(s.Host.Integration.ConnectionId, "synthetic", "account-1", "mailbox@example.invalid");
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync("/api/email-accounts", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync("/api/email-accounts", request with { ConnectionId = Guid.NewGuid(), ExternalAccountId = "other" })).StatusCode);
        var update = new UpdateEmailAccountRequest("renamed@example.invalid", "Display", "disabled", false, 0);
        var response = await s.Client.PutAsJsonAsync($"/api/email-accounts/{s.Account.Id}", update); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<EmailAccountDto>())!;
        Assert.Equal("account-1", updated.ExternalAccountId); Assert.Equal(1, updated.Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PutAsJsonAsync($"/api/email-accounts/{s.Account.Id}", update)).StatusCode);
        var json = await s.Client.GetStringAsync("/api/email-accounts");
        foreach (var field in new[] { "secret", "password", "token", "credential", "canonicalContent" }) Assert.DoesNotContain(field, json, StringComparison.OrdinalIgnoreCase);
        await using var db = Db(); Assert.Equal(1, await db.EmailSyncStates.CountAsync(x => x.EmailAccountId == s.Account.Id));
    }

    [Theory]
    [InlineData("disabled")][InlineData("revoked")][InlineData("archived")]
    public async Task InactiveAccountsCannotEnqueueOrDispatch(string status)
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var update = new UpdateEmailAccountRequest(s.Account.EmailAddress, null, status == "archived" ? "active" : status, status == "archived", 0);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PutAsJsonAsync($"/api/email-accounts/{s.Account.Id}", update)).StatusCode);
        Assert.Equal("EmailAccountUnavailable", (await Assert.ThrowsAsync<EmailSyncException>(() => Dispatch(s))).Message);
        await AssertState(s, "failed", 0, null, "EmailAccountUnavailable");
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/email-accounts/{s.Account.Id}/sync-jobs", new EnqueueEmailSyncRequest())).StatusCode);
        var restore = update with { StatusCode = "active", Archived = false, Revision = 1 };
        Assert.Equal(status == "revoked" ? HttpStatusCode.Conflict : HttpStatusCode.OK,
            (await s.Client.PutAsJsonAsync($"/api/email-accounts/{s.Account.Id}", restore)).StatusCode);
    }

    [Fact]
    public async Task HumanCookieMachineHmacAndCsrfAreSeparated()
    {
        using var s = await Prepare(receive: false); using var anonymous = s.Host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/email-accounts")).StatusCode);
        using var machineHumanRoute = s.Host.Signed([], target: $"/api/email-sync-jobs/{s.Job.Id}/process-result");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(machineHumanRoute)).StatusCode);
        var content = new StringContent(Encoding.UTF8.GetString(s.Body), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.PostAsync("/api/integrations/results", content)).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync($"/api/email-sync-jobs/{s.Job.Id}/process-result", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync($"/api/email-sync-jobs/{s.Job.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/email-accounts", new { })).StatusCode);
        foreach (var key in new[] { "wrong-workspace", "wrong-connection" })
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.SendAsync(s.Host.Signed(s.Body, key))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.SendAsync(s.Host.Signed(s.Body))).StatusCode);
        foreach (var key in new[] { "wrong-workspace", "wrong-connection" })
            Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.SendAsync(s.Host.Signed(s.Body, key))).StatusCode);
    }

    [Fact]
    public async Task WorkspaceIsolationIncludesReadsUpdatesEnqueueProcessingAndReplay()
    {
        using var s = await Prepare(); await Process(s);
        using var other = await SecurityTestHost.CreateAsync(Database); using var client = other.Client(); await SecurityTestHost.LoginAsync(client);
        Assert.Empty((await client.GetFromJsonAsync<EmailAccountDto[]>("/api/email-accounts"))!);
        Assert.Empty((await client.GetFromJsonAsync<EmailMessageDto[]>($"/api/email-messages?emailAccountId={s.Account.Id}"))!);
        foreach (var url in new[] { $"/api/email-sync-jobs/{s.Job.Id}" }) Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/email-sync-jobs/{s.Job.Id}/process-result", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/email-accounts/{s.Account.Id}/sync-jobs", new EnqueueEmailSyncRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/email-accounts/{s.Account.Id}", new UpdateEmailAccountRequest(s.Account.EmailAddress, null, "active", false, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(other.Signed(s.Body))).StatusCode);
        await using var db = Db(); var message = await db.EmailMessages.FirstAsync(x => x.EmailAccountId == s.Account.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/email-messages/{message.Id}")).StatusCode);
    }

    [Theory]
    [InlineData("accessToken")][InlineData("refreshToken")][InlineData("password")][InlineData("workspaceId")][InlineData("credential")]
    public async Task UnknownAccountPropertiesAreRejected(string property)
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var node = new JsonObject { ["connectionId"] = s.Host.Integration.ConnectionId.ToString(), ["providerCode"] = "synthetic",
            ["externalAccountId"] = "other", ["emailAddress"] = "other@example.invalid", [property] = "sensitive-sentinel" };
        var response = await s.Client.PostAsJsonAsync("/api/email-accounts", node); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("sensitive-sentinel", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SignedOversizeBodyAndChangedReplayAreRejected()
    {
        using var s = await Prepare();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await s.Client.SendAsync(s.Host.Signed(new byte[512 * 1024 + 1]))).StatusCode);
        var node = JsonNode.Parse(s.Body)!; node["payload"]!["messages"]![0]!["bodyText"] = "Changed";
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.SendAsync(s.Host.Signed(Encoding.UTF8.GetBytes(node.ToJsonString())))).StatusCode);
        await AssertState(s, "result-received", 0, null);
    }

    [Theory]
    [InlineData("revision")][InlineData("archived")]
    public async Task AccountUpdateRequiresExplicitRevisionAndArchiveDecision(string missing)
    {
        using var s = await Prepare(dispatch: false, receive: false);
        var node = new JsonObject { ["emailAddress"] = s.Account.EmailAddress, ["statusCode"] = "active", ["archived"] = false, ["revision"] = 0 };
        node.Remove(missing);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PutAsJsonAsync($"/api/email-accounts/{s.Account.Id}", node)).StatusCode);
        await using var db = Db(); Assert.Equal(0, (await db.EmailAccounts.SingleAsync(x => x.Id == s.Account.Id)).Revision);
    }
}
