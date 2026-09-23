using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.OpportunitySources;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/opportunities/{opportunityId:guid}/sources")]
public class OpportunitySourcesController(IOpportunitySourceService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<OpportunitySourceDto>>> GetAll(Guid opportunityId, CancellationToken cancellationToken)
    {
        var sources = await service.GetAllAsync(opportunityId, cancellationToken);
        return sources is null ? NotFound() : Ok(sources);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OpportunitySourceDto>> GetById(Guid opportunityId, Guid id, CancellationToken cancellationToken)
    {
        var source = await service.GetByIdAsync(opportunityId, id, cancellationToken);
        return source is null ? NotFound() : Ok(source);
    }

    [HttpPost]
    public async Task<ActionResult<OpportunitySourceDto>> Create(Guid opportunityId,
        CreateOpportunitySourceRequest request, CancellationToken cancellationToken)
    {
        var (found, source, error) = await service.CreateAsync(opportunityId, request, cancellationToken);
        if (!found)
            return NotFound();
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { opportunityId, id = source!.Id }, source);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid opportunityId, Guid id,
        UpdateOpportunitySourceRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await service.UpdateAsync(opportunityId, id, request, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid opportunityId, Guid id, CancellationToken cancellationToken)
        => await service.DeleteAsync(opportunityId, id, cancellationToken) ? NoContent() : NotFound();
}
