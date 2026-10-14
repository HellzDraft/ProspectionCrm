using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Security;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class OperatorDecisionSecurityTests : AutomationActionRequestFixture, IClassFixture<AutomationJobDatabase>
{
    private readonly AutomationJobDatabase database;
    public OperatorDecisionSecurityTests(AutomationJobDatabase database) : base(database) => this.database = database;
    [Theory]
    [InlineData("approve")][InlineData("reject")]
    public async Task DecisionRequiresCookieCsrfAndRecordsAuthenticatedActor(string action)
    {
        var setup = await Prepare("assist"); await Run(setup); var request = await RequestFor(setup.Job.Id);
        using var host = await SecurityTestHost.CreateAsync(database, setup.Workspace);
        using var client = host.Client(); var route = $"/api/automation-action-requests/{request.Id}/{action}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(route, new { })).StatusCode);
        await SecurityTestHost.LoginAsync(client);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new { })).StatusCode);
        await SecurityTestHost.CsrfAsync(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new { decidedByUserId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(route, new { decisionNote = "Local operator" })).StatusCode);
        var decided = await RequestFor(setup.Job.Id);
        Assert.Equal(host.Operator.UserId, decided.DecidedByUserId);
        Assert.Equal(action == "approve" ? "approved" : "rejected", decided.StatusCode);
        Assert.Empty(await Tasks(setup.Job.Id));
        var other = await Prepare("assist"); await Run(other); var foreign = await RequestFor(other.Job.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/automation-action-requests/{foreign.Id}/{action}", new { })).StatusCode);
    }

    [Fact]
    public async Task OwnerRowAloneDoesNotSupplyHumanIdentity()
    {
        var setup = await Prepare("assist"); await Run(setup);
        await using var db = Db();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new HumanActor(new HttpContextAccessor(), db).RequireAsync());
        using var host = await SecurityTestHost.CreateAsync(database, setup.Workspace);
        host.Operator.UserId = Guid.NewGuid();
        using var client = host.Client(); await SecurityTestHost.CsrfAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/session/login", new { password = SecurityTestHost.Password })).StatusCode);
    }
}
