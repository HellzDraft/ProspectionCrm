using ProspectionCrm.Api.Dtos.Experiences;

namespace ProspectionCrm.Api.Services;

public interface IExperienceService
{
    Task<IReadOnlyList<ExperienceDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ExperienceDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(ExperienceDto? Experience, string? Error)> CreateAsync(CreateExperienceRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateExperienceRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
