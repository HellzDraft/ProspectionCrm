using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Pipelines;

public class PipelineStageWriteRequest
{
    private string name = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name
    {
        get => name;
        set => name = value?.Trim() ?? string.Empty;
    }

    [MaxLength(2000)]
    public string? Description { get; set; }

    [Required]
    [RegularExpression("^(active|success|failure)$")]
    public string CategoryCode { get; set; } = string.Empty;
}
