using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

// Only HTTP-facing read services use the temporary V1 current-workspace convention.
public sealed class AutomationSupervisionWorkspace(ProspectionCrmDbContext db, ICurrentWorkspaceProvider provider)
{
    public async Task<AutomationJobResult<AutomationRuntimeSettings>> ResolveAsync(CancellationToken token)
    {
        Guid workspace;
        try { workspace = await provider.GetCurrentWorkspaceIdAsync(token); }
        catch (InvalidOperationException) { return new(null, 409, "WorkspaceUnavailable"); }
        var settings = await db.AutomationRuntimeSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspace, token);
        return settings is null ? new(null, 409, "AutomationSettingsMissing") : new(settings);
    }
}
