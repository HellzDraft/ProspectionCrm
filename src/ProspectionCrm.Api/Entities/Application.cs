namespace ProspectionCrm.Api.Entities;

public class Application
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OpportunityId { get; set; }
    public required string StatusCode { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? ChannelCode { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public Opportunity Opportunity { get; set; } = null!;
}
