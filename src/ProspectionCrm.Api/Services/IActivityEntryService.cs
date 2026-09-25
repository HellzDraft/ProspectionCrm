using ProspectionCrm.Api.Dtos.ActivityEntries;

namespace ProspectionCrm.Api.Services;

public interface IActivityEntryService
{
    Task<(IReadOnlyList<ActivityEntryDto>? Items, string? Error)> GetAllAsync(string? entityTypeCode = null, Guid? entityId = null, string? eventTypeCode = null, string? actorTypeCode = null, Guid? relatedOpportunityId = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default);
    Task<ActivityEntryDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}
