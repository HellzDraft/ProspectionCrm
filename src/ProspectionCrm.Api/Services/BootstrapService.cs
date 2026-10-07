using System.Data;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Setup;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class BootstrapService(ProspectionCrmDbContext dbContext) : IBootstrapService
{
    public async Task<(BootstrapDto? Bootstrap, bool Created, string? Error)> BootstrapAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        // PostgreSQL: serialize setup calls, including across API instances, before inspecting state.
        // Also prevent writes to these tables until the decision and creation are committed.
        // ReadCommitted gives the waiting call a fresh snapshot after the previous call commits.
        // The transaction releases the lock on commit, refusal (dispose/rollback), or failure.
        await dbContext.Database.ExecuteSqlRawAsync(
            "LOCK TABLE \"UserAccounts\", \"Workspaces\" IN SHARE ROW EXCLUSIVE MODE", cancellationToken);

        var users = await dbContext.UserAccounts.AsNoTracking().Take(2).ToListAsync(cancellationToken);
        var workspaces = await dbContext.Workspaces.AsNoTracking().Take(2).ToListAsync(cancellationToken);

        if (users.Count == 1 && workspaces.Count == 1
            && workspaces[0].ArchivedAt is null && workspaces[0].OwnerUserId == users[0].Id)
        {
            if (!await dbContext.AutomationRuntimeSettings.AnyAsync(x => x.WorkspaceId == workspaces[0].Id, cancellationToken))
            {
                dbContext.AutomationRuntimeSettings.Add(new() { WorkspaceId = workspaces[0].Id });
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            // A compatible existing installation is never renamed or reset to the defaults.
            await transaction.CommitAsync(cancellationToken);
            return (ToDto(users[0], workspaces[0]), false, null);
        }

        if (users.Count != 0 || workspaces.Count != 0)
            return (null, false,
                "Bootstrap V1 requires an empty installation or exactly one owner and one active workspace linked to that owner. Existing data was not changed.");

        var owner = new UserAccount
        {
            Email = BootstrapDefaults.OwnerEmail,
            DisplayName = BootstrapDefaults.OwnerDisplayName
        };
        var workspace = new Workspace
        {
            OwnerUser = owner,
            OwnerUserId = owner.Id,
            Name = BootstrapDefaults.WorkspaceName,
            TimeZoneId = BootstrapDefaults.TimeZoneId
        };
        workspace.AutomationRuntimeSettings = new AutomationRuntimeSettings { WorkspaceId = workspace.Id };
        dbContext.UserAccounts.Add(owner);
        dbContext.Workspaces.Add(workspace);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (ToDto(owner, workspace), true, null);
    }

    private static BootstrapDto ToDto(UserAccount owner, Workspace workspace)
        => new(owner.Id, owner.Email, owner.DisplayName, workspace.Id, workspace.Name, workspace.TimeZoneId);
}
