using ProspectionCrm.Api.Dtos.AutomationJobs;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

public sealed record AutomationJobResult<T>(T? Value, int Status = 200, string? Code = null);

// Workspace IDs here are trusted internal inputs; the HTTP boundary always resolves its provider.
public interface IAutomationJobQueue
{
    Task<AutomationJobResult<AutomationJob>> EnqueueAsync(Guid workspaceId, EnqueueAutomationJobRequest request, CancellationToken token);
    Task<AutomationJob?> ClaimAsync(Guid workspaceId, CancellationToken token);
    Task<bool> CompleteAsync(Guid workspaceId, Guid jobId, string leaseOwner, CancellationToken token);
    Task<bool> FailAsync(Guid workspaceId, Guid jobId, string leaseOwner, string? error, CancellationToken token);
    Task<bool> ReleaseAsync(Guid workspaceId, Guid jobId, string leaseOwner, DateTimeOffset? availableAt, CancellationToken token);
    Task<bool> CancelAsync(Guid workspaceId, Guid jobId, CancellationToken token);
    Task<int> RecoverAsync(Guid workspaceId, CancellationToken token);
}
