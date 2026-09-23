using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/email-messages")]
public class EmailMessagesController(IEmailMessageService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EmailMessageDto>>> GetAll(CancellationToken cancellationToken,
        [FromQuery] Guid? opportunityId = null, [FromQuery] Guid? companyId = null,
        [FromQuery] Guid? contactId = null, [FromQuery] string? directionCode = null,
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null)
    {
        var (messages, error) = await service.GetAllAsync(opportunityId, companyId, contactId, directionCode, from, to, cancellationToken);
        return error is null ? Ok(messages) : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EmailMessageDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var message = await service.GetByIdAsync(id, cancellationToken);
        return message is null ? NotFound() : Ok(message);
    }
}
