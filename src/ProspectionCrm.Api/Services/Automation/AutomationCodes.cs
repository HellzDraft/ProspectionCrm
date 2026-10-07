namespace ProspectionCrm.Api.Services.Automation;

public static class AutomationModes
{
    public const string Manual = "manual", Assist = "assist", Automatic = "automatic";
    public static bool IsValid(string? code) => code is Manual or Assist or Automatic;
}
public static class AutomationCategories
{
    public const string General = "general", Email = "email", Application = "application";
    public static bool IsValid(string? code) => code is General or Email or Application;
}
public static class AutomationDecisions
{
    public const string Blocked = "blocked", Manual = "manual", ApprovalRequired = "approval-required", Automatic = "automatic";
}
public static class AutomationReasons
{
    public const string Disabled = "automation-disabled", UnknownCategory = "unknown-category", InvalidMode = "invalid-mode",
        GlobalMode = "global-mode", EmailCap = "email-assist-cap", ApplicationCap = "application-manual-cap";
}
