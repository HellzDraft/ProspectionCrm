namespace ProspectionCrm.Api.Entities;

public class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public ICollection<Workspace> OwnedWorkspaces { get; set; } = new List<Workspace>();
}
