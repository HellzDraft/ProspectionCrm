using ProspectionCrm.Api.Security;

namespace ProspectionCrm.Api.Services;

// HTTP identity only; workers use explicit durable workspace IDs.
public class CurrentWorkspaceProvider(IHumanActor actor) : ICurrentWorkspaceProvider
{
    public async Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default)
    {
        return (await actor.RequireAsync(cancellationToken)).WorkspaceId;
    }
}
