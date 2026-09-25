using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.ActivityEntries;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/activity-entries")]
public class ActivityEntriesController(IActivityEntryService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ActivityEntryDto>>> GetAll(CancellationToken cancellationToken,
        [FromQuery] string? entityTypeCode = null, [FromQuery] Guid? entityId = null, [FromQuery] string? eventTypeCode = null, [FromQuery] string? actorTypeCode = null, [FromQuery] Guid? relatedOpportunityId = null, [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null)
    {
        var (items, error) = await service.GetAllAsync(entityTypeCode, entityId, eventTypeCode, actorTypeCode, relatedOpportunityId, from, to, cancellationToken);
        return error is null ? Ok(items) : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ActivityEntryDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var item = await service.GetByIdAsync(id, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }
}
