using Microsoft.AspNetCore.Mvc;
using ProspectionCrm.Api.Dtos.CandidateProfiles;
using ProspectionCrm.Api.Services;

namespace ProspectionCrm.Api.Controllers;

[ApiController]
[Route("api/candidate-profiles")]
public class CandidateProfilesController(ICandidateProfileService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CandidateProfileDto>>> GetAll(CancellationToken cancellationToken,
        [FromQuery] bool includeArchived = false)
        => Ok(await service.GetAllAsync(includeArchived, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CandidateProfileDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var profile = await service.GetByIdAsync(id, cancellationToken);
        return profile is null ? NotFound() : Ok(profile);
    }

    [HttpPost]
    public async Task<ActionResult<CandidateProfileDto>> Create(CreateCandidateProfileRequest request, CancellationToken cancellationToken)
    {
        var (profile, error) = await service.CreateAsync(request, cancellationToken);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        return CreatedAtAction(nameof(GetById), new { id = profile!.Id }, profile);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateCandidateProfileRequest request, CancellationToken cancellationToken)
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

    [HttpPut("{profileId:guid}/experiences/{experienceId:guid}")]
    public async Task<IActionResult> PutExperience(Guid profileId, Guid experienceId, ProfileItemOrderRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await service.PutExperienceAsync(profileId, experienceId, request.SortOrder, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpDelete("{profileId:guid}/experiences/{experienceId:guid}")]
    public async Task<IActionResult> RemoveExperience(Guid profileId, Guid experienceId, CancellationToken cancellationToken)
        => await service.RemoveExperienceAsync(profileId, experienceId, cancellationToken) ? NoContent() : NotFound();

    [HttpPut("{profileId:guid}/educations/{educationId:guid}")]
    public async Task<IActionResult> PutEducation(Guid profileId, Guid educationId, ProfileItemOrderRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await service.PutEducationAsync(profileId, educationId, request.SortOrder, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpDelete("{profileId:guid}/educations/{educationId:guid}")]
    public async Task<IActionResult> RemoveEducation(Guid profileId, Guid educationId, CancellationToken cancellationToken)
        => await service.RemoveEducationAsync(profileId, educationId, cancellationToken) ? NoContent() : NotFound();

    [HttpPut("{profileId:guid}/projects/{projectId:guid}")]
    public async Task<IActionResult> PutProject(Guid profileId, Guid projectId, ProfileItemOrderRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await service.PutProjectAsync(profileId, projectId, request.SortOrder, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpDelete("{profileId:guid}/projects/{projectId:guid}")]
    public async Task<IActionResult> RemoveProject(Guid profileId, Guid projectId, CancellationToken cancellationToken)
        => await service.RemoveProjectAsync(profileId, projectId, cancellationToken) ? NoContent() : NotFound();

    [HttpPut("{profileId:guid}/skills/{skillId:guid}")]
    public async Task<IActionResult> PutSkill(Guid profileId, Guid skillId, ProfileItemOrderRequest request, CancellationToken cancellationToken)
    {
        var (found, error) = await service.PutSkillAsync(profileId, skillId, request.SortOrder, cancellationToken);
        if (!found)
            return NotFound();
        return error is null ? NoContent() : Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpDelete("{profileId:guid}/skills/{skillId:guid}")]
    public async Task<IActionResult> RemoveSkill(Guid profileId, Guid skillId, CancellationToken cancellationToken)
        => await service.RemoveSkillAsync(profileId, skillId, cancellationToken) ? NoContent() : NotFound();
}
