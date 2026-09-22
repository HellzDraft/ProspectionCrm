using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.Companies;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/companies")]
public class CompaniesController(ICompanyService companyService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CompanyDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CompanyDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await companyService.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<CompanyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompanyDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var company = await companyService.GetByIdAsync(id, cancellationToken);
        return company is null ? NotFound() : Ok(company);
    }

    [HttpPost]
    [ProducesResponseType<CompanyDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CompanyDto>> Create(
        CreateCompanyRequest request, CancellationToken cancellationToken)
    {
        var company = await companyService.CreateAsync(request, cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = company.Id }, company);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id, UpdateCompanyRequest request, CancellationToken cancellationToken)
    {
        var found = await companyService.UpdateAsync(id, request, cancellationToken);
        if (!found)
            return NotFound();

        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
        => await companyService.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
}
