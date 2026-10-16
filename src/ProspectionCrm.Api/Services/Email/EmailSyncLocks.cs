using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Email;

// Every mutation: workspace SHARE -> account -> job -> operation -> receipt -> state -> messages.
// No session lock, no source-collection lease, no transaction across transport.
internal static class EmailSyncLocks
{
    internal static async Task WorkspaceAsync(ProspectionCrmDbContext db, Guid workspace, CancellationToken token)
    {
        var value = await db.Workspaces.FromSqlInterpolated($"""
            SELECT * FROM "Workspaces" WHERE "Id" = {workspace} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (value is null || value.ArchivedAt is not null) throw new EmailSyncException("EmailWorkspaceUnavailable");
    }
    internal static async Task<EmailAccount> AccountAsync(ProspectionCrmDbContext db, Guid workspace, Guid account, CancellationToken token) =>
        await db.EmailAccounts.FromSqlInterpolated($"""
            SELECT * FROM "EmailAccounts" WHERE "Id" = {account} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token) ?? throw new EmailSyncException("EmailAccountNotFound", 404);
    internal static async Task<(EmailAccount Account, EmailSyncJob Job, IntegrationOperation Operation)> JobAsync(
        ProspectionCrmDbContext db, Guid workspace, Guid id, CancellationToken token)
    {
        var accountId = await db.EmailSyncJobs.AsNoTracking().Where(x => x.Id == id && x.WorkspaceId == workspace)
            .Select(x => (Guid?)x.EmailAccountId).SingleOrDefaultAsync(token) ?? throw new EmailSyncException("EmailSyncJobNotFound", 404);
        var account = await AccountAsync(db, workspace, accountId, token);
        var job = await db.EmailSyncJobs.FromSqlInterpolated($"""
            SELECT * FROM "EmailSyncJobs" WHERE "Id" = {id} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).AsNoTracking().SingleAsync(token);
        var op = await db.IntegrationOperations.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationOperations" WHERE "Id" = {job.IntegrationOperationId} FOR UPDATE
            """).AsNoTracking().SingleAsync(token);
        if (op.WorkspaceId != workspace || op.ConnectionId != account.ConnectionId || op.Type != "email.sync"
            || job.ConnectionId != account.ConnectionId || job.OperationType != op.Type) throw new EmailSyncException("EmailSyncBindingMismatch");
        return (account, job, op);
    }
    internal static Task<EmailSyncState> StateAsync(ProspectionCrmDbContext db, Guid account, CancellationToken token) =>
        db.EmailSyncStates.FromSqlInterpolated($"""
            SELECT * FROM "EmailSyncStates" WHERE "EmailAccountId" = {account} FOR UPDATE
            """).AsNoTracking().SingleAsync(token);
    internal static Task<IntegrationReceipt?> ReceiptAsync(ProspectionCrmDbContext db, Guid operation, CancellationToken token) =>
        db.IntegrationReceipts.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationReceipts" WHERE "OperationId" = {operation} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token);
    internal static Task<DateTimeOffset> Now(ProspectionCrmDbContext db, CancellationToken token) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(token);
}
