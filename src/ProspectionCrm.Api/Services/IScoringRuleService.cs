using ProspectionCrm.Api.Dtos.ScoringRules;

namespace ProspectionCrm.Api.Services;

public interface IScoringRuleService
{
    Task<IReadOnlyList<ScoringRuleDto>> GetAllAsync(bool includeArchived = false, Guid? pipelineId = null, bool? enabled = null, CancellationToken cancellationToken = default);
    Task<ScoringRuleDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(ScoringRuleDto? Item, string? Error)> CreateAsync(CreateScoringRuleRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateScoringRuleRequest request, CancellationToken cancellationToken);
    Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> RestoreAsync(Guid id, CancellationToken cancellationToken);
}
