using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.ActivityEntries;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class ActivityEntryService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : IActivityEntryService
{
    public async Task<(IReadOnlyList<ActivityEntryDto>? Items, string? Error)> GetAllAsync(string? entityTypeCode = null, Guid? entityId = null, string? eventTypeCode = null, string? actorTypeCode = null, Guid? relatedOpportunityId = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        if (from.HasValue && to.HasValue && from > to)
            return (null, "From must be less than or equal to To.");
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        from = from?.ToUniversalTime();
        to = to?.ToUniversalTime();
        var entities = await dbContext.ActivityEntries.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId
                && (entityTypeCode == null || x.EntityTypeCode == entityTypeCode)
                && (entityId == null || x.EntityId == entityId)
                && (eventTypeCode == null || x.EventTypeCode == eventTypeCode)
                && (actorTypeCode == null || x.ActorTypeCode == actorTypeCode)
                && (relatedOpportunityId == null || x.RelatedOpportunityId == relatedOpportunityId)
                && (from == null || x.OccurredAt >= from)
                && (to == null || x.OccurredAt <= to))
            .OrderByDescending(x => x.OccurredAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return (entities.Select(ToDto).ToList(), null);
    }

    public async Task<ActivityEntryDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.ActivityEntries.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    private static ActivityEntryDto ToDto(ActivityEntry entity) => new()
    {
        Id = entity.Id,
        RelatedOpportunityId = entity.RelatedOpportunityId,
        EntityTypeCode = entity.EntityTypeCode,
        EntityId = entity.EntityId,
        EventTypeCode = entity.EventTypeCode,
        ActorTypeCode = entity.ActorTypeCode,
        ActorUserId = entity.ActorUserId,
        Summary = entity.Summary,
        MetadataJson = entity.MetadataJson,
        OccurredAt = entity.OccurredAt
    };
}
