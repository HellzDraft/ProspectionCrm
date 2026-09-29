using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Pipelines;

// Shared create/update contract: ownership, lifecycle and child resources are not writable here.
public class PipelineWriteRequest
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
    [MaxLength(50)]
    public string TypeCode { get; set; } = string.Empty;

    public bool IsVisible { get; set; } = true;
}
