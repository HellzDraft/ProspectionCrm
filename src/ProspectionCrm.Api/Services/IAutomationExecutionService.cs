using ProspectionCrm.Api.Dtos.AutomationExecutions;

namespace ProspectionCrm.Api.Services;

public interface IAutomationExecutionService
{
    Task<(IReadOnlyList<AutomationExecutionDto>? Items, string? Error)> GetAllAsync(Guid? automationRuleId = null, string? statusCode = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default);
    Task<AutomationExecutionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}
