using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Opportunities;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/opportunities")]
public class OpportunitiesController(IOpportunityService opportunityService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<OpportunityDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OpportunityDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await opportunityService.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<OpportunityDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OpportunityDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var opportunity = await opportunityService.GetByIdAsync(id, cancellationToken);
        return opportunity is null ? NotFound() : Ok(opportunity);
    }

    [HttpPost]
    [ProducesResponseType<OpportunityDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OpportunityDto>> Create(
        CreateOpportunityRequest request, CancellationToken cancellationToken)
    {
        var (opportunity, error) = await opportunityService.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(GetById), new { id = opportunity!.Id }, opportunity);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id, UpdateOpportunityRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await opportunityService.UpdateAsync(id, request, cancellationToken);
        if (!found)
            return NotFound();
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        => await opportunityService.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
}
