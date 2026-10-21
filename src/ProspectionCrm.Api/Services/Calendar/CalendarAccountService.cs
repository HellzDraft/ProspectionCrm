using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Calendar;

public sealed class CalendarSyncException(string code, int status = 409) : Exception(code)
{ public int Status { get; } = status; }

public sealed class CalendarAccountService(ProspectionCrmDbContext db, IOptionsMonitor<IntegrationOptions> options)
{
    public async Task<List<CalendarAccountDto>> ListAsync(Guid workspace, CancellationToken token) =>
        (await db.CalendarAccounts.AsNoTracking().Where(x => x.WorkspaceId == workspace).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(token)).Select(Dto).ToList();

    public async Task<CalendarAccountDto> CreateAsync(Guid workspace, CreateCalendarAccountRequest request, CancellationToken token)
    {
        Validate(request.DisplayName);
        if (request.ProviderCode is null || !Regex.IsMatch(request.ProviderCode, "\\A[a-z][a-z0-9-]{0,49}\\z")
            || string.IsNullOrWhiteSpace(request.ExternalAccountId) || request.ExternalAccountId.Length > 500
            || request.ExternalAccountId != request.ExternalAccountId.Trim() || request.ExternalAccountId.Any(char.IsControl))
            throw new CalendarSyncException("CalendarAccountInvalid", 400);
        RequireConnection(workspace, request.ConnectionId);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await CalendarSyncLocks.WorkspaceAsync(db, workspace, token);
        var now = await CalendarSyncLocks.Now(db, token);
        var id = Guid.NewGuid();
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "CalendarAccounts" ("Id","WorkspaceId","ConnectionId","ProviderCode","ExternalAccountId","DisplayName","StatusCode","Revision","CreatedAt")
            VALUES ({id},{workspace},{request.ConnectionId},{request.ProviderCode},{request.ExternalAccountId},{request.DisplayName},'active',0,{now})
            ON CONFLICT ("WorkspaceId","ProviderCode","ExternalAccountId") DO NOTHING
            """, token);
        if (inserted == 0) throw new CalendarSyncException("CalendarAccountAlreadyExists");
        var account = await db.CalendarAccounts.AsNoTracking().SingleAsync(x => x.Id == id, token);
        await tx.CommitAsync(token); return Dto(account);
    }

    public async Task<CalendarAccountDto> UpdateAsync(Guid workspace, Guid id, UpdateCalendarAccountRequest request, CancellationToken token)
    {
        Validate(request.DisplayName);
        if (request.StatusCode is not ("active" or "disabled" or "revoked") || request.Revision < 0) throw new CalendarSyncException("CalendarAccountInvalid", 400);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await CalendarSyncLocks.WorkspaceAsync(db, workspace, token);
        var account = await CalendarSyncLocks.AccountAsync(db, workspace, id, token);
        if (account.Revision != request.Revision) throw new CalendarSyncException("CalendarAccountRevisionConflict");
        // Revocation is final for this stable identity in 9.6.1; reconnect is a later explicit workflow.
        if (account.StatusCode == "revoked" && request.StatusCode != "revoked") throw new CalendarSyncException("CalendarAccountRevoked");
        var now = await CalendarSyncLocks.Now(db, token);
        DateTimeOffset? archived = request.Archived ? account.ArchivedAt ?? now : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "CalendarAccounts" SET "DisplayName" = {request.DisplayName},
                "StatusCode" = {request.StatusCode}, "ArchivedAt" = {archived}, "UpdatedAt" = {now}, "Revision" = "Revision" + 1 WHERE "Id" = {id}
            """, token);
        account = await db.CalendarAccounts.AsNoTracking().SingleAsync(x => x.Id == id, token);
        await tx.CommitAsync(token); return Dto(account);
    }
    internal void RequireConnection(Guid workspace, Guid connection)
    {
        var config = options.CurrentValue;
        if (connection == Guid.Empty || config.WorkspaceId != workspace || config.ConnectionId != connection)
            throw new CalendarSyncException("CalendarConnectionMismatch");
    }
    private static void Validate(string? display)
    {
        if (display is not null && (display.Length > 200 || display.Any(char.IsControl)))
            throw new CalendarSyncException("CalendarAccountInvalid", 400);
    }
    private static CalendarAccountDto Dto(CalendarAccount x) => new(x.Id, x.ConnectionId, x.ProviderCode, x.ExternalAccountId,
        x.DisplayName, x.StatusCode, x.Revision, x.CreatedAt, x.UpdatedAt, x.ArchivedAt);
}
