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
        var result = await service.CreateAsync(opportunityId, request, cancellationToken);
        return result.Status == OpportunitySourceWriteStatus.Succeeded
            ? CreatedAtAction(nameof(GetById), new { opportunityId, id = result.Source!.Id }, result.Source)
            : Failure(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid opportunityId, Guid id,
        UpdateOpportunitySourceRequest request, CancellationToken cancellationToken)
    {
        var result = await service.UpdateAsync(opportunityId, id, request, cancellationToken);
        return result.Status == OpportunitySourceWriteStatus.Succeeded ? NoContent() : Failure(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid opportunityId, Guid id, CancellationToken cancellationToken)
    {
        var result = await service.DeleteAsync(opportunityId, id, cancellationToken);
        return result.Status == OpportunitySourceWriteStatus.Succeeded ? NoContent() : Failure(result);
    }

    private ObjectResult Failure(OpportunitySourceWriteResult result) => Problem(
        statusCode: result.Status switch
        {
            OpportunitySourceWriteStatus.NotFound => 404,
            OpportunitySourceWriteStatus.InvalidInput => 400,
            _ => 409
        },
        detail: result.Detail,
        extensions: new Dictionary<string, object?> { ["code"] = result.Code?.ToString() });
}
