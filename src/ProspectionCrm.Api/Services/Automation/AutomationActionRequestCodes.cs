namespace ProspectionCrm.Api.Services.Automation;

public static class ActionRequestStatuses
{
    public const string Pending = "pending", Approved = "approved", Rejected = "rejected", Cancelled = "cancelled";
    public static bool IsValid(string? code) => code is Pending or Approved or Rejected or Cancelled;
}

public static class ActionRequestCodes
{
    public const int PlanBytes = 65536, NoteLength = 2000;
    public const string Stale = "action-request-stale", InvalidPlan = "action-request-invalid-plan",
        Unavailable = "action-request-unavailable", Mismatch = "action-request-mismatch", HumanApproved = "human-approved";
    public static bool RequiresDecision(string? code) => code is AutomationDecisions.Manual or AutomationDecisions.ApprovalRequired;
}
