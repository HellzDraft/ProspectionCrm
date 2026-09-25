using ProspectionCrm.Api.Dtos.AutomationRules;

namespace ProspectionCrm.Api.Services;

public interface IAutomationRuleService
{
    Task<IReadOnlyList<AutomationRuleDto>> GetAllAsync(bool includeArchived = false, Guid? pipelineId = null, bool? enabled = null, string? triggerTypeCode = null, CancellationToken cancellationToken = default);
    Task<AutomationRuleDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(AutomationRuleDto? Item, string? Error)> CreateAsync(CreateAutomationRuleRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateAutomationRuleRequest request, CancellationToken cancellationToken);
    Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> RestoreAsync(Guid id, CancellationToken cancellationToken);
}
