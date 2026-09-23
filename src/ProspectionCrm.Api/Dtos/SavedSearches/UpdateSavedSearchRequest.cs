using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.SavedSearches;

public class UpdateSavedSearchRequest
{
    [Required]
    public Guid? PipelineId { get; set; }

    [Required]
    public Guid? SourceConfigurationId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? SearchUrl { get; set; }

    [Required]
    public string CriteriaJson { get; set; } = "{}";

    public bool Enabled { get; set; } = true;
}
