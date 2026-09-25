using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Educations;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class EducationService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : IEducationService
{
    public async Task<IReadOnlyList<EducationDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entities = await dbContext.Educations.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderBy(x => x.StartedOn == null).ThenByDescending(x => x.StartedOn).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<EducationDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Educations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(EducationDto? Education, string? Error)> CreateAsync(CreateEducationRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (request.StartedOn.HasValue && request.EndedOn.HasValue && request.EndedOn.Value < request.StartedOn.Value)
            return (null, "EndedOn must be greater than or equal to StartedOn.");
        var entity = new Education
        {
            WorkspaceId = workspaceId,
            InstitutionName = request.InstitutionName,
            Degree = request.Degree,
            FieldOfStudy = request.FieldOfStudy,
            Description = request.Description,
            StartedOn = request.StartedOn,
            EndedOn = request.EndedOn
        };
        dbContext.Educations.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(entity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateEducationRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Educations.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        if (request.StartedOn.HasValue && request.EndedOn.HasValue && request.EndedOn.Value < request.StartedOn.Value)
            return (true, "EndedOn must be greater than or equal to StartedOn.");
        entity.InstitutionName = request.InstitutionName;
        entity.Degree = request.Degree;
        entity.FieldOfStudy = request.FieldOfStudy;
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
        var entity = await dbContext.Educations.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        if (await dbContext.CandidateProfileEducations.AnyAsync(x => x.EducationId == id && x.Education.WorkspaceId == workspaceId, cancellationToken))
            return (true, "This education is still referenced and cannot be deleted.");
        dbContext.Educations.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private static EducationDto ToDto(Education entity) => new()
    {
        Id = entity.Id,
        InstitutionName = entity.InstitutionName,
        Degree = entity.Degree,
        FieldOfStudy = entity.FieldOfStudy,
        Description = entity.Description,
        StartedOn = entity.StartedOn,
        EndedOn = entity.EndedOn,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };
}
