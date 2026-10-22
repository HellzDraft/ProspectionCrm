using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Integrations;
using ProspectionCrm.Api.Entities;
namespace ProspectionCrm.Api.Services.Calendar;
internal sealed record CalendarImportResult(int Created, int Existing, int Updated, string? ConflictId);
public sealed class InboundCalendarImportService(ProspectionCrmDbContext db)
{
    internal async Task<CalendarImportResult> ImportAsync(Entities.Calendar calendar, IReadOnlyList<InboundCalendarEvent> events, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Calendar import requires a transaction.");
        var ids = events.Select(x => x.ExternalEventId).ToArray();
        var series = events.Where(x => x.SeriesId is not null).Select(x => x.SeriesId!).Distinct().ToArray();
        var stored = await db.CalendarEvents.FromSqlInterpolated($"""
            SELECT * FROM "CalendarEvents" WHERE "CalendarId"={calendar.Id}
                AND ("ExternalEventId"=ANY({ids}) OR "SeriesId"=ANY({series})) ORDER BY "ExternalEventId" FOR UPDATE
            """).AsNoTracking().ToDictionaryAsync(x => x.ExternalEventId!, token);
        // Validate every revision and immutable recurrence identity before writing any event.
        foreach (var item in events)
        {
            if (item.SeriesId is not null && stored.Values.Any(x => x.SeriesId == item.SeriesId && x.OccurrenceId == item.OccurrenceId && x.ExternalEventId != item.ExternalEventId))
                return new(0, 0, 0, item.ExternalEventId);
            if (!stored.TryGetValue(item.ExternalEventId, out var old)) continue;
            var oldValue = new InboundCalendarEvent(old.ExternalEventId!, old.ExternalRevision!.Value, old.Title, old.Description, old.Location,
                old.StartsAt, old.EndsAt, old.IsAllDay, old.TimeZoneId, old.StatusCode!, old.SeriesId, old.OccurrenceId);
            if (old.ProviderCode != calendar.ProviderCode || old.ExternalContentSha256 != Hash(oldValue)
                || item.Revision < old.ExternalRevision || item.SeriesId != old.SeriesId || item.OccurrenceId != old.OccurrenceId
                || item.Revision == old.ExternalRevision && Hash(item) != old.ExternalContentSha256)
                return new(0, 0, 0, item.ExternalEventId);
        }
        var created = 0; var existing = 0; var updated = 0;
        foreach (var item in events)
        {
            var hash = Hash(item);
            if (stored.TryGetValue(item.ExternalEventId, out var old))
            {
                if (old.ExternalRevision == item.Revision) { existing++; continue; }
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "CalendarEvents" SET "Title"={item.Title}, "Description"={item.Description}, "Location"={item.Location},
                        "StartsAt"={item.StartsAt}, "EndsAt"={item.EndsAt}, "IsAllDay"={item.IsAllDay}, "TimeZoneId"={item.TimeZoneId},
                        "StatusCode"={item.Status}, "ExternalRevision"={item.Revision}, "ExternalContentSha256"={hash}, "UpdatedAt"=statement_timestamp()
                    WHERE "Id"={old.Id}
                    """, token); updated++;
            }
            else
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "CalendarEvents" ("Id","WorkspaceId","CalendarId","ProviderCode","ExternalEventId","Title","Description","Location",
                        "StartsAt","EndsAt","IsAllDay","TimeZoneId","StatusCode","ExternalRevision","ExternalContentSha256","SeriesId","OccurrenceId","CreatedAt")
                    VALUES ({Guid.NewGuid()},{calendar.WorkspaceId},{calendar.Id},{calendar.ProviderCode},{item.ExternalEventId},{item.Title},{item.Description},{item.Location},
                        {item.StartsAt},{item.EndsAt},{item.IsAllDay},{item.TimeZoneId},{item.Status},{item.Revision},{hash},{item.SeriesId},{item.OccurrenceId},statement_timestamp())
                    """, token); created++;
            }
        }
        return new(created, existing, updated, null);
    }
    internal static string Hash(InboundCalendarEvent value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
