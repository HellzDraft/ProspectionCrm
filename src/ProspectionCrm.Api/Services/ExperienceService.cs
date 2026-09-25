using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Experiences;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class ExperienceService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : IExperienceService
{
    public async Task<IReadOnlyList<ExperienceDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entities = await dbContext.Experiences.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderBy(x => x.StartedOn == null).ThenByDescending(x => x.StartedOn).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<ExperienceDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Experiences.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(ExperienceDto? Experience, string? Error)> CreateAsync(CreateExperienceRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (request.StartedOn.HasValue && request.EndedOn.HasValue && request.EndedOn.Value < request.StartedOn.Value)
            return (null, "EndedOn must be greater than or equal to StartedOn.");
        var entity = new Experience
        {
            WorkspaceId = workspaceId,
            Title = request.Title,
            OrganizationName = request.OrganizationName,
            Location = request.Location,
            Description = request.Description,
            StartedOn = request.StartedOn,
            EndedOn = request.EndedOn
        };
        dbContext.Experiences.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(entity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateExperienceRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Experiences.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        if (request.StartedOn.HasValue && request.EndedOn.HasValue && request.EndedOn.Value < request.StartedOn.Value)
            return (true, "EndedOn must be greater than or equal to StartedOn.");
        entity.Title = request.Title;
        entity.OrganizationName = request.OrganizationName;
        entity.Location = request.Location;
        entity.Description = request.Description;
        entity.StartedOn = request.StartedOn;
        entity.EndedOn = request.EndedOn;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<(bool Found, string? Error)> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Experiences.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        if (await dbContext.CandidateProfileExperiences.AnyAsync(x => x.ExperienceId == id && x.Experience.WorkspaceId == workspaceId, cancellationToken))
            return (true, "This experience is still referenced and cannot be deleted.");
        dbContext.Experiences.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private static ExperienceDto ToDto(Experience entity) => new()
    {
        Id = entity.Id,
        Title = entity.Title,
        OrganizationName = entity.OrganizationName,
        Location = entity.Location,
        Description = entity.Description,
        StartedOn = entity.StartedOn,
        EndedOn = entity.EndedOn,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };
}
