using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Pipelines;

public class PipelineCloneRequest
{
    private string name = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name
    {
        get => name;
        set => name = value?.Trim() ?? string.Empty;
    }
}
