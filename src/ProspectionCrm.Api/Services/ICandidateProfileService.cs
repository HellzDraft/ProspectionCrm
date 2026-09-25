using ProspectionCrm.Api.Dtos.CandidateProfiles;

namespace ProspectionCrm.Api.Services;

public interface ICandidateProfileService
{
    Task<IReadOnlyList<CandidateProfileDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<CandidateProfileDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(CandidateProfileDto? Profile, string? Error)> CreateAsync(CreateCandidateProfileRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateCandidateProfileRequest request, CancellationToken cancellationToken);
    Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> RestoreAsync(Guid id, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> PutExperienceAsync(Guid profileId, Guid experienceId, int sortOrder, CancellationToken cancellationToken);
    Task<bool> RemoveExperienceAsync(Guid profileId, Guid experienceId, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> PutEducationAsync(Guid profileId, Guid educationId, int sortOrder, CancellationToken cancellationToken);
    Task<bool> RemoveEducationAsync(Guid profileId, Guid educationId, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> PutProjectAsync(Guid profileId, Guid projectId, int sortOrder, CancellationToken cancellationToken);
    Task<bool> RemoveProjectAsync(Guid profileId, Guid projectId, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> PutSkillAsync(Guid profileId, Guid skillId, int sortOrder, CancellationToken cancellationToken);
    Task<bool> RemoveSkillAsync(Guid profileId, Guid skillId, CancellationToken cancellationToken);
}
