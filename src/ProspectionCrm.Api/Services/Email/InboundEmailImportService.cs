using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Email;

internal sealed record EmailImportResult(int Created, int Existing, string? ConflictId);

public sealed class InboundEmailImportService(ProspectionCrmDbContext db)
{
    // Only the sync processor calls this, under the account lock and its outer transaction.
    // Compare the WHOLE batch before inserting anything: a controlled conflict commits zero new messages.
    internal async Task<EmailImportResult> ImportAsync(EmailAccount account, IReadOnlyList<InboundEmail> messages, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Email import requires a transaction.");
        var ids = messages.Select(x => x.ExternalMessageId).ToArray();
        var stored = await db.EmailMessages.FromSqlInterpolated($"""
            SELECT * FROM "EmailMessages" WHERE "WorkspaceId" = {account.WorkspaceId} AND "EmailAccountId" = {account.Id}
                AND "ExternalMessageId" = ANY({ids}) ORDER BY "ExternalMessageId" FOR UPDATE
            """).AsNoTracking().ToDictionaryAsync(x => x.ExternalMessageId!, token);
        foreach (var message in messages)
            if (stored.TryGetValue(message.ExternalMessageId, out var old) && !Equal(old, message, account.ProviderCode))
                return new(0, 0, message.ExternalMessageId);
        foreach (var message in messages.Where(x => !stored.ContainsKey(x.ExternalMessageId)))
        {
            var to = JsonSerializer.Serialize(message.ToAddresses); var cc = JsonSerializer.Serialize(message.CcAddresses ?? []);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "EmailMessages" ("Id","WorkspaceId","EmailAccountId","ProviderCode","ExternalMessageId","ExternalThreadId",
                    "DirectionCode","FromAddress","ToAddressesJson","CcAddressesJson","Subject","BodyText","OccurredAt","CreatedAt")
                VALUES ({Guid.NewGuid()},{account.WorkspaceId},{account.Id},{account.ProviderCode},{message.ExternalMessageId},{message.ExternalThreadId},
                    'inbound',{message.FromAddress},CAST({to} AS jsonb),CAST({cc} AS jsonb),{message.Subject},{message.BodyText},{message.OccurredAt},statement_timestamp())
                """, token);
        }
        return new(messages.Count - stored.Count, stored.Count, null);
    }
    private static bool Equal(EmailMessage old, InboundEmail value, string provider)
    {
        try
        {
            return old.ProviderCode == provider && old.DirectionCode == "inbound" && old.ExternalThreadId == value.ExternalThreadId
                && old.FromAddress == value.FromAddress && old.Subject == value.Subject && old.BodyText == value.BodyText && old.OccurredAt == value.OccurredAt
                && EmailSyncContracts.NormalizeAddresses(JsonSerializer.Deserialize<string[]>(old.ToAddressesJson)!).SequenceEqual(value.ToAddresses)
                && EmailSyncContracts.NormalizeAddresses(JsonSerializer.Deserialize<string[]>(old.CcAddressesJson ?? "[]")!).SequenceEqual(value.CcAddresses ?? []);
        }
        catch (Exception e) when (e is JsonException or ArgumentException) { return false; }
    }
}
