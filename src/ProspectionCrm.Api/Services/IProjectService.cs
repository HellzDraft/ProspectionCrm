using ProspectionCrm.Api.Dtos.Projects;

namespace ProspectionCrm.Api.Services;

public interface IProjectService
{
    Task<IReadOnlyList<ProjectDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<ProjectDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(ProjectDto? Project, string? Error)> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken);
    Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> PutSkillAsync(Guid projectId, Guid skillId, CancellationToken cancellationToken);
    Task<bool> RemoveSkillAsync(Guid projectId, Guid skillId, CancellationToken cancellationToken);
}
