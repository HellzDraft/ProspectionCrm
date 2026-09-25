namespace ProspectionCrm.Api.Dtos.Pipelines;

public class PipelineDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string TypeCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public IReadOnlyList<PipelineStageDto> Stages { get; set; } = [];
    public Guid? PreferredCandidateProfileId { get; set; }
}
