using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.AutomationActionRequests;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services;
using ProspectionCrm.Api.Services.Automation;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public abstract class AutomationActionRequestFixture(AutomationJobDatabase database) : AutomationRuntimeFixture(database)
{
    protected async Task<AutomationActionRequest> RequestFor(Guid job)
    {
        await using var db = Db();
        return await db.AutomationActionRequests.AsNoTracking().SingleAsync(x => x.AutomationJobId == job);
    }
    protected async Task<AutomationJobResult<AutomationActionRequestDto>> Decide(Setup setup, bool approve = true,
        string? note = null, IInterceptor? interceptor = null, CancellationToken token = default)
    {
        var request = await RequestFor(setup.Job.Id);
        await using var db = interceptor is null ? Db() : InterceptedDb(interceptor);
        return await Service(db, setup.Workspace).DecideAsync(request.Id, new() { DecisionNote = note }, approve, token);
    }
    protected AutomationActionRequestService Service(ProspectionCrmDbContext db, Guid workspace)
    {
        var runtime = Runtime(db);
        return new(db, new WorkspaceProvider(workspace), runtime, new(db, runtime));
    }
    private sealed class WorkspaceProvider(Guid workspace) : ICurrentWorkspaceProvider
    {
        public Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(workspace);
    }
    protected async Task<Setup> Approved(string mode = "manual")
    {
        var setup = await Prepare(mode); await Run(setup);
        Assert.Equal(200, (await Decide(setup)).Status); return setup;
    }
}
