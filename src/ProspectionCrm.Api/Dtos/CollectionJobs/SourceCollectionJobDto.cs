using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ProspectionCrm.Api.Dtos.CollectionJobs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EnqueueSourceCollectionJobRequest : IValidatableObject
{
    [Required] public Guid? PipelineStageId { get; init; }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (PipelineStageId == Guid.Empty)
            yield return new ValidationResult("A valid pipelineStageId is required.", [nameof(PipelineStageId)]);
    }
}

public sealed record SourceCollectionJobDto(Guid Id, Guid WorkspaceId, Guid SavedSearchId, Guid PipelineId,
    Guid PipelineStageId, string TriggerTypeCode, string StatusCode, DateTimeOffset EnqueuedAt,
    DateTimeOffset AvailableAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int AttemptCount,
    Guid? SourceExecutionId, string? ErrorCode);

public sealed record SourceCollectionJobsPageDto(int Offset, int Limit, int TotalCount, bool HasMore,
    IReadOnlyList<SourceCollectionJobDto> Items);
