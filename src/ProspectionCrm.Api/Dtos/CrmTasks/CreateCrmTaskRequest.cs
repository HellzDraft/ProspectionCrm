using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.CrmTasks;

public class CreateCrmTaskRequest
{
    [Required]
    public Guid? OpportunityId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(10000)]
    public string? Description { get; set; }

    public DateTimeOffset? DueAt { get; set; }
}
