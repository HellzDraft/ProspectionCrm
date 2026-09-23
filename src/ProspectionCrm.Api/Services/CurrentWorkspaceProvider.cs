using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services;

// Temporary V1 selection, independent of authentication and without implicit bootstrap.
public class CurrentWorkspaceProvider(ProspectionCrmDbContext dbContext) : ICurrentWorkspaceProvider
{
    public async Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default)
    {
        var ids = await dbContext.Workspaces.AsNoTracking()
            .Where(workspace => workspace.ArchivedAt == null)
            .Select(workspace => workspace.Id).Take(2).ToListAsync(cancellationToken);
        return ids.Count switch
        {
            1 => ids[0],
            0 => throw new InvalidOperationException("Aucun Workspace actif : la V1 attend exactement un Workspace actif."),
            _ => throw new InvalidOperationException("Plusieurs Workspaces actifs : la V1 attend exactement un Workspace actif.")
        };
    }
}
