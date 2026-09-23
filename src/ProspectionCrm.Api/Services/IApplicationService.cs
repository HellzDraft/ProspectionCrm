using ProspectionCrm.Api.Dtos.Applications;

namespace ProspectionCrm.Api.Services;

public interface IApplicationService
{
    Task<IReadOnlyList<ApplicationDto>?> GetAllAsync(Guid opportunityId, CancellationToken cancellationToken);
    Task<ApplicationDto?> GetByIdAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken);
    Task<(bool Found, ApplicationDto? Application, string? Error)> CreateAsync(Guid opportunityId,
        CreateApplicationRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid opportunityId, Guid id,
        UpdateApplicationRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken);
}
