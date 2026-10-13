namespace ProspectionCrm.Api.Entities;

public sealed class IntegrationAttempt
{
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public int Number { get; set; }
    public required string StatusCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
