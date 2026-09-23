namespace ProspectionCrm.Api.Dtos.Applications;

public class ApplicationDto
{
    public Guid Id { get; set; }
    public Guid OpportunityId { get; set; }
    public string StatusCode { get; set; } = string.Empty;
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? ChannelCode { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
