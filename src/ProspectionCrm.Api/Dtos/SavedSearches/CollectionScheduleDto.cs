using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ProspectionCrm.Api.Dtos.SavedSearches;

public sealed record CollectionScheduleDto(bool Enabled, string? DailyUtcTime, Guid? PipelineStageId, DateTimeOffset? NextCollectionAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UpdateCollectionScheduleRequest
{
    [Required]
    public bool? Enabled { get; set; }
    [RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    public string? DailyUtcTime { get; set; }
    public Guid? PipelineStageId { get; set; }
}
