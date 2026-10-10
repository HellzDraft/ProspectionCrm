using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.AutomationSupervision;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class AutomationCircuitResetTests(AutomationJobDatabase database)
    : AutomationSupervisionFixture(database), IClassFixture<AutomationJobDatabase>
{
    [Theory]
    [InlineData(null)][InlineData("   ")][InlineData("Incident corrigé.")]
    public async Task ResetIsAnAuditedMarkerAndDoesNotChangeBusinessState(string? note)
    {
        var setup = await Prepare(); await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = 1);
        await Outcome(setup, 1, true, Clock.Now.AddSeconds(-30)); await Outcome(setup, 2, false);
        await Settings(setup.Workspace, s => s.IsEnabled = false);
        var snapshot = await BusinessSnapshot(setup.Workspace); var quotas = (await Supervise(setup.Workspace)).Quotas;
        var reset = await Reset(setup.Workspace, note);
        Assert.True(reset.WasReset); Assert.Equal("closed", reset.Status.StatusCode);
        Assert.Equal(2, reset.Reset!.ResetAfterOutcomeSequence); Assert.Equal(Clock.Now, reset.Reset.RequestedAt);
        Assert.Equal(TimeSpan.Zero, reset.Reset.RequestedAt.Offset);
        Assert.Equal(string.IsNullOrWhiteSpace(note) ? null : note, reset.Reset.Note);
        await using var db = Db();
        Assert.Equal((await db.Workspaces.SingleAsync(x => x.Id == setup.Workspace)).OwnerUserId, reset.Reset.RequestedByUserId);
        Assert.Equal(snapshot, await BusinessSnapshot(setup.Workspace));
        Assert.Equal(quotas, (await Supervise(setup.Workspace)).Quotas);
        Clock.Now = Clock.Now.AddHours(1);
        var repeated = await Reset(setup.Workspace, "Ignored");
        Assert.False(repeated.WasReset); Assert.Null(repeated.Reset);
        Assert.Equal(reset.Status.LastResetId, repeated.Status.LastResetId);
        Assert.Equal(reset.Status.LastResetAt, repeated.Status.LastResetAt);
        Assert.Equal(reset.Status.LastResetNote, repeated.Status.LastResetNote);
        Assert.Single(await db.AutomationCircuitResets.Where(x => x.WorkspaceId == setup.Workspace).ToListAsync());
        Assert.Equal(snapshot, await BusinessSnapshot(setup.Workspace));
    }

    [Fact]
    public async Task ConcurrentResetsSerializeAndClosedResetDoesNotInsert()
    {
        var setup = await Prepare();
        Assert.False((await Reset(setup.Workspace)).WasReset);
        await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = 1); await Outcome(setup, 1, false);
        var resets = await Task.WhenAll(Reset(setup.Workspace, "A"), Reset(setup.Workspace, "B"));
        Assert.Single(resets, x => x.WasReset); Assert.Single(resets, x => !x.WasReset);
        Assert.All(resets, x => Assert.Equal("closed", x.Status.StatusCode));
        await using var db = Db(); Assert.Single(await db.AutomationCircuitResets.Where(x => x.WorkspaceId == setup.Workspace).ToListAsync());
    }

    [Theory]
    [InlineData("workspaceId")][InlineData("requestedByUserId")][InlineData("resetAfterOutcomeSequence")]
    [InlineData("requestedAt")][InlineData("isEnabled")][InlineData("statusCode")]
    public async Task ClientCannotChooseAuthorityFields(string field)
    {
        var setup = await Prepare();
        using var factory = SupervisionFactory(setup.Workspace); using var client = Client(factory);
        var result = await client.PostAsync("/api/automation-circuit-breaker/reset",
            new StringContent("{\"" + field + "\":\"secret-marker\"}", Encoding.UTF8, "application/json"));
        await Problem(result, HttpStatusCode.BadRequest, "InvalidRequest");
        await using var db = Db(); Assert.Empty(await db.AutomationCircuitResets.Where(x => x.WorkspaceId == setup.Workspace).ToListAsync());
    }

    [Fact]
    public async Task HttpResetHistoryIsScopedPaginatedAndNotesAreValidated()
    {
        var a = await Prepare(); var b = await Prepare();
        await Settings(a.Workspace, s => s.MaxConsecutiveFailures = 1);
        await Settings(b.Workspace, s => s.MaxConsecutiveFailures = 1);
        await Outcome(b, 1, false); var foreign = await Reset(b.Workspace, "Foreign");
        using var factory = SupervisionFactory(a.Workspace); using var client = Client(factory);
        var root = "/api/automation-circuit-breaker";
        await Problem(await client.PostAsJsonAsync(root + "/reset", new { note = new string('x', 2001) }),
            HttpStatusCode.BadRequest, "InvalidResetNote");
        for (var i = 1; i <= 3; i++)
        {
            await Outcome(a, i, false); Clock.Now = Clock.Now.AddSeconds(1);
            var response = await client.PostAsJsonAsync(root + "/reset", new { note = "Reset " + i });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await response.Content.ReadFromJsonAsync<AutomationCircuitResetResult>())!.WasReset);
        }
        var before = await BusinessSnapshot(a.Workspace);
        var page = (await client.GetFromJsonAsync<AutomationCircuitResetsPage>(root + "/resets?offset=1&limit=1"))!;
        Assert.Equal(3, page.TotalCount); Assert.True(page.HasMore); Assert.Equal(2, Assert.Single(page.Items).ResetAfterOutcomeSequence);
        Assert.DoesNotContain(page.Items, x => x.Id == foreign.Reset!.Id);
        Assert.Equal("closed", (await client.GetFromJsonAsync<AutomationCircuitStatus>(root))!.StatusCode);
        var again = await client.PostAsJsonAsync(root + "/reset", new { note = "Ignored" });
        Assert.False((await again.Content.ReadFromJsonAsync<AutomationCircuitResetResult>())!.WasReset);
        Assert.Equal(before, await BusinessSnapshot(a.Workspace));
    }

    [Theory]
    [InlineData(2000, true)][InlineData(2001, false)]
    public async Task ResetNoteBoundaryIsEnforcedBeforeAnyMutation(int length, bool accepted)
    {
        var setup = await Prepare(); await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = 1);
        await Outcome(setup, 1, false);
        using var factory = SupervisionFactory(setup.Workspace); using var client = Client(factory);
        var response = await client.PostAsJsonAsync("/api/automation-circuit-breaker/reset", new { note = new string('x', length) });
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(accepted ? "closed" : "open", (await Circuit(setup.Workspace)).StatusCode);
        await using var db = Db();
        Assert.Equal(accepted ? 1 : 0, await db.AutomationCircuitResets.CountAsync(x => x.WorkspaceId == setup.Workspace));
    }

    [Fact]
    public async Task ResetWaitsForTheRuntimeLockAndIncludesAnOutcomeCommittedBeforeIt()
    {
        var setup = await Prepare(); await Settings(setup.Workspace, s => s.MaxConsecutiveFailures = 1);
        await Outcome(setup, 1, false);
        var job = await Another(setup);
        await using var db = Db();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await Runtime(db).LockAsync(setup.Workspace, default);
        var pendingReset = Reset(setup.Workspace);
        var e = new ProspectionCrm.Api.Entities.AutomationExecution {
            WorkspaceId = setup.Workspace, AutomationRuleId = setup.Rule.Id, AutomationJobId = job.Id,
            AttemptNumber = 1, IsAutomaticAttempt = true, OutcomeSequence = 2, StatusCode = "failed",
            TriggeredAt = Clock.Now, StartedAt = Clock.Now, FinishedAt = Clock.Now };
        db.Add(e); await db.SaveChangesAsync();
        Assert.False(pendingReset.IsCompleted);
        await transaction.CommitAsync();
        var reset = await pendingReset.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(2, reset.Reset!.ResetAfterOutcomeSequence); Assert.Equal("closed", reset.Status.StatusCode);
        Assert.Equal(2, await db.AutomationExecutions.CountAsync(x => x.WorkspaceId == setup.Workspace));
    }

    [Theory]
    [InlineData("offset=-1")][InlineData("limit=0")][InlineData("limit=201")][InlineData("offset=invalid")]
    public async Task InvalidHistoryPaginationReturnsControlledErrors(string query)
    {
        var setup = await Prepare(); using var factory = SupervisionFactory(setup.Workspace); using var client = Client(factory);
        await Problem(await client.GetAsync("/api/automation-circuit-breaker/resets?" + query),
            HttpStatusCode.BadRequest, query.Contains("invalid") ? "InvalidRequest" : "InvalidPagination");
    }
}
