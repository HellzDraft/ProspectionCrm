using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Proposals;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/opportunities/{opportunityId:guid}/proposals")]
public class ProposalsController(IProposalService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProposalDto>>> GetAll(Guid opportunityId, CancellationToken cancellationToken)
    {
        var entities = await service.GetAllAsync(opportunityId, cancellationToken);
        return entities is null ? NotFound() : Ok(entities);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProposalDto>> GetById(Guid opportunityId, Guid id, CancellationToken cancellationToken)
    {
        var entity = await service.GetByIdAsync(opportunityId, id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity);
    }

    [HttpPost]
    public async Task<ActionResult<ProposalDto>> Create(Guid opportunityId,
        CreateProposalRequest request, CancellationToken cancellationToken)
    {
        var (found, entity, error) = await service.CreateAsync(opportunityId, request, cancellationToken);
        if (!found)
            return NotFound();
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { opportunityId, id = entity!.Id }, entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid opportunityId, Guid id,
        UpdateProposalRequest request, CancellationToken cancellationToken)
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
