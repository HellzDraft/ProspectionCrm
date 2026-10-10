using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

internal static class AutomationExecutionQueries
{
    // Shared by recovery and supervision. Legacy unlinked histories are not runtime attempts.
    internal static IQueryable<AutomationExecution> Abandoned(ProspectionCrmDbContext db, Guid workspace) =>
        db.AutomationExecutions.FromSqlInterpolated($"""
            SELECT e.* FROM "AutomationExecutions" e
            JOIN "AutomationJobs" j ON j."Id" = e."AutomationJobId" AND j."WorkspaceId" = e."WorkspaceId"
            WHERE e."WorkspaceId" = {workspace} AND e."StatusCode" = 'running'
                AND (j."StatusCode" <> 'leased' OR e."AttemptNumber" <> j."AttemptCount"
                    OR j."LeaseExpiresAt" <= statement_timestamp())
            """);
}
