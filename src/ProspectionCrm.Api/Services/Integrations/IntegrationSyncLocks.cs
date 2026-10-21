using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;
namespace ProspectionCrm.Api.Services.Integrations;
internal static class IntegrationSyncLocks
{
    internal static Task<IntegrationReceipt?> ReceiptAsync(ProspectionCrmDbContext db, Guid operation, CancellationToken token) =>
        db.IntegrationReceipts.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationReceipts" WHERE "OperationId" = {operation} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);
    internal static Task<DateTimeOffset> Now(ProspectionCrmDbContext db, CancellationToken token) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(token);
}
