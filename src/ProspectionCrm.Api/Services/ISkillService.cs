using ProspectionCrm.Api.Dtos.Skills;

namespace ProspectionCrm.Api.Services;

public interface ISkillService
{
    Task<IReadOnlyList<SkillDto>> GetAllAsync(string? categoryCode = null, CancellationToken cancellationToken = default);
    Task<SkillDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(SkillDto? Skill, string? Error)> CreateAsync(CreateSkillRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateSkillRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
