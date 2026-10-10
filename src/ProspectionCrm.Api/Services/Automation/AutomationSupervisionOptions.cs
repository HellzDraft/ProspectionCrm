namespace ProspectionCrm.Api.Services.Automation;

public sealed class AutomationSupervisionOptions
{
    public const string SectionName = "AutomationSupervision";
    public int PendingJobWarningAgeMinutes { get; set; } = 30;
    public int ApprovalWarningAgeHours { get; set; } = 24;
    public int BacklogWarningCount { get; set; } = 100;
    public int RecentFailureWindowHours { get; set; } = 24;
    public bool IsValid() => PendingJobWarningAgeMinutes is >= 1 and <= 10080
        && ApprovalWarningAgeHours is >= 1 and <= 8760
        && BacklogWarningCount is >= 1 and <= 1000000
        && RecentFailureWindowHours is >= 1 and <= 8760;
}
