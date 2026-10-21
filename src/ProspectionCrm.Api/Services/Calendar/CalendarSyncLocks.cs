using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Calendar;

// Every mutation: workspace SHARE -> account -> calendar -> job -> operation -> receipt -> state -> messages.
// No session lock, no source-collection lease, no transaction across transport.
internal static class CalendarSyncLocks
{
    internal static async Task WorkspaceAsync(ProspectionCrmDbContext db, Guid workspace, CancellationToken token)
    {
        var value = await db.Workspaces.FromSqlInterpolated($"""
            SELECT * FROM "Workspaces" WHERE "Id" = {workspace} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (value is null || value.ArchivedAt is not null) throw new CalendarSyncException("CalendarWorkspaceUnavailable");
    }
    internal static async Task<CalendarAccount> AccountAsync(ProspectionCrmDbContext db, Guid workspace, Guid account, CancellationToken token) =>
        await db.CalendarAccounts.FromSqlInterpolated($"""
            SELECT * FROM "CalendarAccounts" WHERE "Id" = {account} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(token) ?? throw new CalendarSyncException("CalendarAccountNotFound", 404);
    internal static async Task<(CalendarAccount Account, Entities.Calendar Calendar, CalendarSyncJob Job, IntegrationOperation Operation)> JobAsync(
        ProspectionCrmDbContext db, Guid workspace, Guid id, CancellationToken token)
    {
        var calendarId = await db.CalendarSyncJobs.AsNoTracking().Where(x => x.Id == id && x.WorkspaceId == workspace)
            .Select(x => (Guid?)x.CalendarId).SingleOrDefaultAsync(token) ?? throw new CalendarSyncException("CalendarSyncJobNotFound", 404);
        var (account, calendar) = await CalendarAsync(db, workspace, calendarId, token);
        var job = await db.CalendarSyncJobs.FromSqlInterpolated($"""
            SELECT * FROM "CalendarSyncJobs" WHERE "Id" = {id} AND "WorkspaceId" = {workspace} FOR UPDATE
            """).AsNoTracking().SingleAsync(token);
        var op = await db.IntegrationOperations.FromSqlInterpolated($"""
            SELECT * FROM "IntegrationOperations" WHERE "Id" = {job.IntegrationOperationId} FOR UPDATE
            """).AsNoTracking().SingleAsync(token);
        if (op.WorkspaceId != workspace || op.ConnectionId != account.ConnectionId || op.Type != "calendar.sync"
            || job.ConnectionId != account.ConnectionId || job.OperationType != op.Type) throw new CalendarSyncException("CalendarSyncBindingMismatch");
        return (account, calendar, job, op);
    }
    internal static async Task<(CalendarAccount Account, Entities.Calendar Calendar)> CalendarAsync(ProspectionCrmDbContext db, Guid workspace, Guid id, CancellationToken token)
    {
        var accountId = await db.Calendars.AsNoTracking().Where(x => x.Id == id && x.WorkspaceId == workspace)
            .Select(x => (Guid?)x.CalendarAccountId).SingleOrDefaultAsync(token) ?? throw new CalendarSyncException("CalendarNotFound", 404);
        var account = await AccountAsync(db, workspace, accountId, token);
        var calendar = await db.Calendars.FromSqlInterpolated($"""
            SELECT * FROM "Calendars" WHERE "Id"={id} AND "WorkspaceId"={workspace} FOR UPDATE
            """).AsNoTracking().SingleAsync(token);
        return (account, calendar);
    }
    internal static Task<CalendarSyncState> StateAsync(ProspectionCrmDbContext db, Guid account, CancellationToken token) =>
        db.CalendarSyncStates.FromSqlInterpolated($"""
            SELECT * FROM "CalendarSyncStates" WHERE "CalendarId" = {account} FOR UPDATE
            """).AsNoTracking().SingleAsync(token);
    internal static Task<IntegrationReceipt?> ReceiptAsync(ProspectionCrmDbContext db, Guid operation, CancellationToken token) =>
        Services.Integrations.IntegrationSyncLocks.ReceiptAsync(db, operation, token);
    internal static Task<DateTimeOffset> Now(ProspectionCrmDbContext db, CancellationToken token) =>
        Services.Integrations.IntegrationSyncLocks.Now(db, token);
}
