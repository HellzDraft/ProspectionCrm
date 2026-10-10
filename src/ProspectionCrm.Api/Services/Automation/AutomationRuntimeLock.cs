using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services.Automation;

internal static class AutomationRuntimeLock
{
    internal static Task AcquireAsync(ProspectionCrmDbContext db, Guid workspace, CancellationToken token)
    {
        var key = $"automation-runtime:{workspace:N}";
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);
    }
}
