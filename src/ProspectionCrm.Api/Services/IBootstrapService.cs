using ProspectionCrm.Api.Dtos.Setup;

namespace ProspectionCrm.Api.Services;

public interface IBootstrapService
{
    Task<(BootstrapDto? Bootstrap, bool Created, string? Error)> BootstrapAsync(CancellationToken cancellationToken);
}
