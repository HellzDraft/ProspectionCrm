using ProspectionCrm.Api.Dtos.Companies;

namespace ProspectionCrm.Api.Services;

public interface ICompanyService
{
    Task<IReadOnlyList<CompanyDto>> GetAllAsync(CancellationToken cancellationToken);
    Task<CompanyDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<CompanyDto> CreateAsync(
        CreateCompanyRequest request, CancellationToken cancellationToken);
    Task<bool> UpdateAsync(
        Guid id, UpdateCompanyRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
