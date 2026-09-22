using ProspectionCrm.Api.Dtos.CrmTasks;

namespace ProspectionCrm.Api.Services;

public interface ICrmTaskService
{
    Task<IReadOnlyList<CrmTaskDto>> GetAllAsync(
        Guid? opportunityId, bool? isCompleted, CancellationToken cancellationToken);
    Task<CrmTaskDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<CrmTaskDto>> GetDueAsync(CancellationToken cancellationToken);
    Task<(CrmTaskDto? CrmTask, string? Error)> CreateAsync(
        CreateCrmTaskRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(
        Guid id, UpdateCrmTaskRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> ReopenAsync(Guid id, CancellationToken cancellationToken);
}
