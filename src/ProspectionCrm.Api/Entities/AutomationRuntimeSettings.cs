using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Entities;

public class AutomationRuntimeSettings
{
    public Guid WorkspaceId { get; set; }
    public bool IsEnabled { get; set; }
    public string OperatingModeCode { get; set; } = AutomationModes.Manual;
    public int MaxExecutionsPerMinute { get; set; } = 10;
    public int MaxExecutionsPerDay { get; set; } = 100;
    public int MaxConsecutiveFailures { get; set; } = 3;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public Workspace Workspace { get; set; } = null!;
}
