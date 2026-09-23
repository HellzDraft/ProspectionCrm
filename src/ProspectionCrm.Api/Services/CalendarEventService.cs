using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class CalendarEventService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : ICalendarEventService
{
    public async Task<(IReadOnlyList<CalendarEventDto>? Events, string? Error)> GetAllAsync(
        Guid? opportunityId = null, Guid? contactId = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        from = from?.ToUniversalTime();
        to = to?.ToUniversalTime();
        if (from.HasValue && to.HasValue && from.Value > to.Value)
            return (null, "The 'from' date must be less than or equal to the 'to' date.");

        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var events = await dbContext.CalendarEvents.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId
                && (!opportunityId.HasValue || x.OpportunityId == opportunityId.Value)
                && (!contactId.HasValue || x.ContactId == contactId.Value)
                && (!from.HasValue || x.EndsAt >= from.Value)
                && (!to.HasValue || x.StartsAt <= to.Value))
            .OrderBy(x => x.StartsAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return (events.Select(ToDto).ToList(), null);
    }

    public async Task<CalendarEventDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var calendarEvent = await dbContext.CalendarEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return calendarEvent is null ? null : ToDto(calendarEvent);
    }

    // Future provider ingestion must validate that all CRM references belong to this workspace.
    private static CalendarEventDto ToDto(CalendarEvent calendarEvent) => new()
    {
        Id = calendarEvent.Id,
        OpportunityId = calendarEvent.OpportunityId,
        ContactId = calendarEvent.ContactId,
        ProviderCode = calendarEvent.ProviderCode,
        ExternalEventId = calendarEvent.ExternalEventId,
        Title = calendarEvent.Title,
        Description = calendarEvent.Description,
        Location = calendarEvent.Location,
        StartsAt = calendarEvent.StartsAt,
        EndsAt = calendarEvent.EndsAt,
        IsAllDay = calendarEvent.IsAllDay,
        TimeZoneId = calendarEvent.TimeZoneId,
        CreatedAt = calendarEvent.CreatedAt,
        UpdatedAt = calendarEvent.UpdatedAt
    };
}
