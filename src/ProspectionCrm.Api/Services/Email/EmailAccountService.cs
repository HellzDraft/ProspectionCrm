using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
using ProspectionCrm.Api.Services.Integrations;

namespace ProspectionCrm.Api.Services.Email;

public sealed class EmailSyncException(string code, int status = 409) : Exception(code)
{ public int Status { get; } = status; }

public sealed class EmailAccountService(ProspectionCrmDbContext db, IOptionsMonitor<IntegrationOptions> options)
{
    public async Task<List<EmailAccountDto>> ListAsync(Guid workspace, CancellationToken token) =>
        (await db.EmailAccounts.AsNoTracking().Where(x => x.WorkspaceId == workspace).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(token)).Select(Dto).ToList();

    public async Task<EmailAccountDto> CreateAsync(Guid workspace, CreateEmailAccountRequest request, CancellationToken token)
    {
        Validate(request.EmailAddress, request.DisplayName);
        if (request.ProviderCode is null || !Regex.IsMatch(request.ProviderCode, "\\A[a-z][a-z0-9-]{0,49}\\z")
            || string.IsNullOrWhiteSpace(request.ExternalAccountId) || request.ExternalAccountId.Length > 500
            || request.ExternalAccountId != request.ExternalAccountId.Trim() || request.ExternalAccountId.Any(char.IsControl))
            throw new EmailSyncException("EmailAccountInvalid", 400);
        RequireConnection(workspace, request.ConnectionId);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await EmailSyncLocks.WorkspaceAsync(db, workspace, token);
        var now = await EmailSyncLocks.Now(db, token);
        var id = Guid.NewGuid();
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "EmailAccounts" ("Id","WorkspaceId","ConnectionId","ProviderCode","ExternalAccountId","EmailAddress","DisplayName","StatusCode","Revision","CreatedAt")
            VALUES ({id},{workspace},{request.ConnectionId},{request.ProviderCode},{request.ExternalAccountId},{request.EmailAddress},{request.DisplayName},'active',0,{now})
            ON CONFLICT ("WorkspaceId","ProviderCode","ExternalAccountId") DO NOTHING
            """, token);
        if (inserted == 0) throw new EmailSyncException("EmailAccountAlreadyExists");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "EmailSyncStates" ("EmailAccountId","WorkspaceId","UpdatedAt","Revision") VALUES ({id},{workspace},{now},0)
            """, token);
        var account = await db.EmailAccounts.AsNoTracking().SingleAsync(x => x.Id == id, token);
        await tx.CommitAsync(token); return Dto(account);
    }

    public async Task<EmailAccountDto> UpdateAsync(Guid workspace, Guid id, UpdateEmailAccountRequest request, CancellationToken token)
    {
        Validate(request.EmailAddress, request.DisplayName);
        if (request.StatusCode is not ("active" or "disabled" or "revoked") || request.Revision < 0) throw new EmailSyncException("EmailAccountInvalid", 400);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await EmailSyncLocks.WorkspaceAsync(db, workspace, token);
        var account = await EmailSyncLocks.AccountAsync(db, workspace, id, token);
        if (account.Revision != request.Revision) throw new EmailSyncException("EmailAccountRevisionConflict");
        // Revocation is final for this stable identity in 9.5.1; reconnect is a later explicit workflow.
        if (account.StatusCode == "revoked" && request.StatusCode != "revoked") throw new EmailSyncException("EmailAccountRevoked");
        var now = await EmailSyncLocks.Now(db, token);
        DateTimeOffset? archived = request.Archived ? account.ArchivedAt ?? now : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "EmailAccounts" SET "EmailAddress" = {request.EmailAddress}, "DisplayName" = {request.DisplayName},
                "StatusCode" = {request.StatusCode}, "ArchivedAt" = {archived}, "UpdatedAt" = {now}, "Revision" = "Revision" + 1 WHERE "Id" = {id}
            """, token);
        account = await db.EmailAccounts.AsNoTracking().SingleAsync(x => x.Id == id, token);
        await tx.CommitAsync(token); return Dto(account);
    }
    internal void RequireConnection(Guid workspace, Guid connection)
    {
        var config = options.CurrentValue;
        if (connection == Guid.Empty || config.WorkspaceId != workspace || config.ConnectionId != connection)
            throw new EmailSyncException("EmailConnectionMismatch");
    }
    private static void Validate(string address, string? display)
    {
        if (!EmailSyncContracts.IsAddress(address) || display is not null && (display.Length > 200 || display.Any(char.IsControl)))
            throw new EmailSyncException("EmailAccountInvalid", 400);
    }
    private static EmailAccountDto Dto(EmailAccount x) => new(x.Id, x.ConnectionId, x.ProviderCode, x.ExternalAccountId, x.EmailAddress,
        x.DisplayName, x.StatusCode, x.Revision, x.CreatedAt, x.UpdatedAt, x.ArchivedAt);
}
