using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services.Automation;

public interface IAutomationWorkSource
{
    Task<IReadOnlyList<Guid>> FindAsync(Guid? after, int limit, bool expired, CancellationToken token);
}

// Keyset traversal bounds each cycle and prevents a busy low-ID workspace starving later ones.
public sealed class AutomationWorkSource(ProspectionCrmDbContext db) : IAutomationWorkSource
{
    public async Task<IReadOnlyList<Guid>> FindAsync(Guid? after, int limit, bool expired, CancellationToken token)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return await db.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT "WorkspaceId" AS "Value" FROM "AutomationJobs"
            WHERE ({after}::uuid IS NULL OR "WorkspaceId" > {after}::uuid)
                AND (({expired} AND (("StatusCode" = 'leased' AND "LeaseExpiresAt" <= statement_timestamp())
                    OR EXISTS (SELECT 1 FROM "AutomationExecutions" e WHERE e."AutomationJobId" = "AutomationJobs"."Id"
                        AND e."StatusCode" = 'running' AND ("AutomationJobs"."StatusCode" <> 'leased'
                            OR e."AttemptNumber" <> "AutomationJobs"."AttemptCount"))))
                    OR (NOT {expired} AND "StatusCode" = 'pending' AND "AvailableAt" <= statement_timestamp()))
            ORDER BY "Value" LIMIT {limit}
            """).ToListAsync(token);
    }
}
