using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Pipelines;

public class PipelineStageOrderRequest
{
    [Required]
    public Guid[]? StageIds { get; set; }
}
