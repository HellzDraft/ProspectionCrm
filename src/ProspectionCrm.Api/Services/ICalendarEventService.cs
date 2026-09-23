using ProspectionCrm.Api.Dtos.CalendarEvents;

namespace ProspectionCrm.Api.Services;

public interface ICalendarEventService
{
    Task<(IReadOnlyList<CalendarEventDto>? Events, string? Error)> GetAllAsync(
        Guid? opportunityId = null, Guid? contactId = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default);
    Task<CalendarEventDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}
