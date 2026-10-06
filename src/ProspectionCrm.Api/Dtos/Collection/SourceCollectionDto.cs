using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ProspectionCrm.Api.Dtos.Ingestions;

namespace ProspectionCrm.Api.Dtos.Collection;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CollectSavedSearchRequest
{
    [Required] public Guid? PipelineStageId { get; init; }
}
public sealed record SourceCollectionDto(IngestionDto Ingestion, SourceCollectionSummaryDto Summary);
public sealed record SourceCollectionSummaryDto(string SourceTypeCode, string FeedFormat, int EntriesRead,
    int EntriesMapped, int EntriesSkipped, bool WasTruncated);
