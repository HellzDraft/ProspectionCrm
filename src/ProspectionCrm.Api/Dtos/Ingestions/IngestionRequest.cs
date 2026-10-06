using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ProspectionCrm.Api.Dtos.Ingestions;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class IngestionRequest
{
    [Required]
    public Guid? PipelineStageId { get; init; }

    [Required, MinLength(1), MaxLength(100)]
    public List<IngestionItemRequest>? Items { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class IngestionItemRequest
{
    [Required, MaxLength(200)]
    public string Title { get; init; } = string.Empty;
    [MaxLength(500)]
    public string? ExternalId { get; init; }
    [MaxLength(2048)]
    public string? SourceUrl { get; init; }
    [MaxLength(200)]
    public string? CompanyName { get; init; }
    [MaxLength(200)]
    public string? Location { get; init; }
    [MaxLength(10000)]
    public string? Description { get; init; }
}
