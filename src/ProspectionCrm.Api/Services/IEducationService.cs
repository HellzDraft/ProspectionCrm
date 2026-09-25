using ProspectionCrm.Api.Dtos.Educations;

namespace ProspectionCrm.Api.Services;

public interface IEducationService
{
    Task<IReadOnlyList<EducationDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<EducationDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(EducationDto? Education, string? Error)> CreateAsync(CreateEducationRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateEducationRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
