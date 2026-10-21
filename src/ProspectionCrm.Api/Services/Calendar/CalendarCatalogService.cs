using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using CalendarEntity = ProspectionCrm.Api.Entities.Calendar;
using ProspectionCrm.Api.Entities;
namespace ProspectionCrm.Api.Services.Calendar;
public sealed class CalendarCatalogService(ProspectionCrmDbContext db, CalendarAccountService accounts)
{
    public async Task<List<CalendarDto>> ListAsync(Guid workspace, CancellationToken token) =>
        (await db.Calendars.AsNoTracking().Where(x => x.WorkspaceId == workspace).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(token)).Select(Dto).ToList();
    public async Task<CalendarDto> CreateAsync(Guid workspace, Guid accountId, CreateCalendarRequest request, CancellationToken token)
    {
        Validate(request.DisplayName, request.TimeZoneId);
        if (string.IsNullOrWhiteSpace(request.ExternalCalendarId) || request.ExternalCalendarId.Length > 500
            || request.ExternalCalendarId != request.ExternalCalendarId.Trim() || request.ExternalCalendarId.Any(char.IsControl)) throw new CalendarSyncException("CalendarInvalid", 400);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await CalendarSyncLocks.WorkspaceAsync(db, workspace, token);
        var account = await CalendarSyncLocks.AccountAsync(db, workspace, accountId, token);
        accounts.RequireConnection(workspace, account.ConnectionId);
        if (account.StatusCode != "active" || account.ArchivedAt is not null) throw new CalendarSyncException("CalendarAccountUnavailable");
        if (await db.Calendars.AnyAsync(x => x.CalendarAccountId == accountId && x.ExternalCalendarId == request.ExternalCalendarId, token)) throw new CalendarSyncException("CalendarAlreadyExists");
        var now = await CalendarSyncLocks.Now(db, token);
        var calendar = new CalendarEntity { WorkspaceId = workspace, CalendarAccountId = accountId, ConnectionId = account.ConnectionId,
            ProviderCode = account.ProviderCode, ExternalCalendarId = request.ExternalCalendarId, DisplayName = request.DisplayName, TimeZoneId = request.TimeZoneId, CreatedAt = now };
        db.Add(calendar); db.Add(new CalendarSyncState { CalendarId = calendar.Id, WorkspaceId = workspace, UpdatedAt = now });
        await db.SaveChangesAsync(token); await tx.CommitAsync(token); return Dto(calendar);
    }
    public async Task<CalendarDto> UpdateAsync(Guid workspace, Guid id, UpdateCalendarRequest request, CancellationToken token)
    {
        Validate(request.DisplayName, request.TimeZoneId);
        if (request.StatusCode is not ("active" or "disabled") || request.Revision < 0) throw new CalendarSyncException("CalendarInvalid", 400);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await CalendarSyncLocks.WorkspaceAsync(db, workspace, token);
        var (_, calendar) = await CalendarSyncLocks.CalendarAsync(db, workspace, id, token);
        if (calendar.Revision != request.Revision) throw new CalendarSyncException("CalendarRevisionConflict");
        var now = await CalendarSyncLocks.Now(db, token); DateTimeOffset? archived = request.Archived ? calendar.ArchivedAt ?? now : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Calendars" SET "DisplayName"={request.DisplayName}, "TimeZoneId"={request.TimeZoneId}, "StatusCode"={request.StatusCode},
                "ArchivedAt"={archived}, "UpdatedAt"={now}, "Revision"="Revision"+1 WHERE "Id"={id}
            """, token);
        calendar = await db.Calendars.AsNoTracking().SingleAsync(x => x.Id == id, token);
        await tx.CommitAsync(token); return Dto(calendar);
    }
    private static void Validate(string? display, string? zone)
    {
        if (display is not null && (display.Length > 200 || display.Any(char.IsControl))) throw new CalendarSyncException("CalendarInvalid", 400);
        _ = CalendarSyncContracts.Zone(zone);
    }
    private static CalendarDto Dto(CalendarEntity x) => new(x.Id, x.CalendarAccountId, x.ExternalCalendarId, x.DisplayName, x.TimeZoneId,
        x.StatusCode, x.Revision, x.CreatedAt, x.UpdatedAt, x.ArchivedAt);
}
