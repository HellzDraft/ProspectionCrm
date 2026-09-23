namespace ProspectionCrm.Api.Services;

public interface ICurrentWorkspaceProvider
{
    Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken cancellationToken = default);
}
