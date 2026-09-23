using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.CalendarEvents;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/calendar-events")]
public class CalendarEventsController(ICalendarEventService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CalendarEventDto>>> GetAll(CancellationToken cancellationToken,
        [FromQuery] Guid? opportunityId = null, [FromQuery] Guid? contactId = null,
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null)
    {
        var (events, error) = await service.GetAllAsync(opportunityId, contactId, from, to, cancellationToken);
        return error is null ? Ok(events) : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CalendarEventDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var calendarEvent = await service.GetByIdAsync(id, cancellationToken);
        return calendarEvent is null ? NotFound() : Ok(calendarEvent);
    }
}
