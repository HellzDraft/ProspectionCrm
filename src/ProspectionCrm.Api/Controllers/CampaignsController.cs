using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Campaigns;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/campaigns")]
public class CampaignsController(ICampaignService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CampaignDto>>> GetAll(CancellationToken cancellationToken,
        [FromQuery] bool includeArchived = false)
        => Ok(await service.GetAllAsync(includeArchived, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CampaignDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await service.GetByIdAsync(id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity);
    }

    [HttpPost]
    public async Task<ActionResult<CampaignDto>> Create(CreateCampaignRequest request, CancellationToken cancellationToken)
    {
        var (entity, error) = await service.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { id = entity!.Id }, entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateCampaignRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await service.UpdateAsync(id, request, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpPost("{id:guid}/archive")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
        => await service.ArchiveAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
    {
        var (found, error) = await service.RestoreAsync(id, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpPost("{campaignId:guid}/opportunities/{opportunityId:guid}")]
    public async Task<IActionResult> AddOpportunity(Guid campaignId, Guid opportunityId, CancellationToken cancellationToken)
    {
        var (found, error) = await service.AddOpportunityAsync(campaignId, opportunityId, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpDelete("{campaignId:guid}/opportunities/{opportunityId:guid}")]
    public async Task<IActionResult> RemoveOpportunity(Guid campaignId, Guid opportunityId, CancellationToken cancellationToken)
        => await service.RemoveOpportunityAsync(campaignId, opportunityId, cancellationToken) ? NoContent() : NotFound();
}
