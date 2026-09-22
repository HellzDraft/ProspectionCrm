using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Contacts;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/contacts")]
public class ContactsController(IContactService contactService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ContactDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ContactDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await contactService.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ContactDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ContactDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var contact = await contactService.GetByIdAsync(id, cancellationToken);
        return contact is null ? NotFound() : Ok(contact);
    }

    [HttpPost]
    [ProducesResponseType<ContactDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ContactDto>> Create(
        CreateContactRequest request, CancellationToken cancellationToken)
    {
        var (contact, error) = await contactService.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(GetById), new { id = contact!.Id }, contact);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id, UpdateContactRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await contactService.UpdateAsync(id, request, cancellationToken);
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
        => await contactService.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
}
