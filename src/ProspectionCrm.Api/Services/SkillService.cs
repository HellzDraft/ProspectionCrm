using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Skills;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class SkillService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : ISkillService
{
    public async Task<IReadOnlyList<SkillDto>> GetAllAsync(string? categoryCode = null, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entities = await dbContext.Skills.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && (categoryCode == null || x.CategoryCode == categoryCode))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<SkillDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Skills.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(SkillDto? Skill, string? Error)> CreateAsync(CreateSkillRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (await dbContext.Skills.AnyAsync(x => x.WorkspaceId == workspaceId && x.Name == request.Name, cancellationToken))
            return (null, "A skill with this name already exists in the current workspace.");
        var entity = new Skill
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            CategoryCode = request.CategoryCode
        };
        dbContext.Skills.Add(entity);
        var error = await SaveSkillAsync(entity, cancellationToken);
        return error is null ? (ToDto(entity), null) : (null, error);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateSkillRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Skills.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        if (await dbContext.Skills.AnyAsync(x => x.WorkspaceId == workspaceId && x.Name == request.Name && x.Id != id, cancellationToken))
            return (true, "A skill with this name already exists in the current workspace.");
        entity.Name = request.Name;
        entity.CategoryCode = request.CategoryCode;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        var error = await SaveSkillAsync(entity, cancellationToken);
        return (true, error);
    }

    public async Task<(bool Found, string? Error)> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Skills.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        if (await dbContext.CandidateProfileSkills.AnyAsync(x => x.SkillId == id && x.Skill.WorkspaceId == workspaceId, cancellationToken)
            || await dbContext.ProjectSkills.AnyAsync(x => x.SkillId == id && x.Skill.WorkspaceId == workspaceId, cancellationToken))
            return (true, "This skill is still referenced and cannot be deleted.");
        dbContext.Skills.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private async Task<string?> SaveSkillAsync(Skill entity, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Skills_WorkspaceId_Name" })
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            return "A skill with this name already exists in the current workspace.";
        }
    }

    private static SkillDto ToDto(Skill entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        CategoryCode = entity.CategoryCode,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };
}
