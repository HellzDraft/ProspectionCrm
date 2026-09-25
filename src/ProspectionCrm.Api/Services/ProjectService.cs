using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Projects;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class ProjectService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : IProjectService
{
    public async Task<IReadOnlyList<ProjectDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entities = await dbContext.Projects.AsNoTracking().Include(x => x.Skills.Where(link => link.Skill.WorkspaceId == workspaceId))
            .Where(x => x.WorkspaceId == workspaceId && (includeArchived || x.ArchivedAt == null))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<ProjectDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Projects.AsNoTracking().Include(x => x.Skills.Where(link => link.Skill.WorkspaceId == workspaceId))
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(ProjectDto? Project, string? Error)> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (request.StartedOn.HasValue && request.EndedOn.HasValue && request.EndedOn.Value < request.StartedOn.Value)
            return (null, "EndedOn must be greater than or equal to StartedOn.");
        var entity = new Project
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            Description = request.Description,
            Role = request.Role,
            RepositoryUrl = request.RepositoryUrl,
            WebsiteUrl = request.WebsiteUrl,
            StartedOn = request.StartedOn,
            EndedOn = request.EndedOn
        };
        dbContext.Projects.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(entity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Projects.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        if (request.StartedOn.HasValue && request.EndedOn.HasValue && request.EndedOn.Value < request.StartedOn.Value)
            return (true, "EndedOn must be greater than or equal to StartedOn.");
        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.Role = request.Role;
        entity.RepositoryUrl = request.RepositoryUrl;
        entity.WebsiteUrl = request.WebsiteUrl;
        entity.StartedOn = request.StartedOn;
        entity.EndedOn = request.EndedOn;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken)
        => SetArchivedAsync(id, true, cancellationToken);

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
        => SetArchivedAsync(id, false, cancellationToken);

    private async Task<bool> SetArchivedAsync(Guid id, bool archive, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Projects.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return false;
        var now = DateTimeOffset.UtcNow;
        entity.ArchivedAt = archive ? now : null;
        entity.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<(bool Found, string? Error)> PutSkillAsync(Guid projectId, Guid skillId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var project = await dbContext.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId && x.WorkspaceId == workspaceId, cancellationToken);
        if (project is null || !await dbContext.Skills.AnyAsync(x => x.Id == skillId && x.WorkspaceId == workspaceId, cancellationToken))
            return (false, null);
        if (await dbContext.ProjectSkills.AnyAsync(x => x.ProjectId == projectId && x.SkillId == skillId
                && x.Project.WorkspaceId == workspaceId && x.Skill.WorkspaceId == workspaceId, cancellationToken))
            return (true, null);
        if (project.ArchivedAt.HasValue)
            return (true, "Cannot add a skill to an archived project.");
        var link = new ProjectSkill { ProjectId = projectId, SkillId = skillId };
        dbContext.ProjectSkills.Add(link);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_ProjectSkills_ProjectId_SkillId" })
        {
            dbContext.Entry(link).State = EntityState.Detached;
        }
        return (true, null);
    }

    public async Task<bool> RemoveSkillAsync(Guid projectId, Guid skillId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.Projects.AnyAsync(x => x.Id == projectId && x.WorkspaceId == workspaceId, cancellationToken)
            || !await dbContext.Skills.AnyAsync(x => x.Id == skillId && x.WorkspaceId == workspaceId, cancellationToken))
            return false;
        var link = await dbContext.ProjectSkills.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.SkillId == skillId
            && x.Project.WorkspaceId == workspaceId && x.Skill.WorkspaceId == workspaceId, cancellationToken);
        if (link is not null)
        {
            dbContext.ProjectSkills.Remove(link);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    private static ProjectDto ToDto(Project entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Description = entity.Description,
        Role = entity.Role,
        RepositoryUrl = entity.RepositoryUrl,
        WebsiteUrl = entity.WebsiteUrl,
        StartedOn = entity.StartedOn,
        EndedOn = entity.EndedOn,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        ArchivedAt = entity.ArchivedAt,
        SkillIds = entity.Skills.Select(x => x.SkillId).OrderBy(x => x).ToList()
    };
}
