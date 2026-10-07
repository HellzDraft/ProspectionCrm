using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

// A null effective mode means no action may be executed or prepared.
public sealed record AutomationSafetyDecision(string CategoryCode, string RequestedModeCode,
    string? EffectiveModeCode, string DecisionCode, string ReasonCode);

public interface IAutomationSafetyPolicy
{
    AutomationSafetyDecision Evaluate(AutomationRuntimeSettings settings, string categoryCode);
}

public sealed class AutomationSafetyPolicy : IAutomationSafetyPolicy
{
    public AutomationSafetyDecision Evaluate(AutomationRuntimeSettings settings, string categoryCode)
    {
        var requested = settings.OperatingModeCode;
        AutomationSafetyDecision Block(string reason) => new(categoryCode, requested, null, AutomationDecisions.Blocked, reason);
        if (!AutomationCategories.IsValid(categoryCode)) return Block(AutomationReasons.UnknownCategory);
        if (!settings.IsEnabled) return Block(AutomationReasons.Disabled);
        if (!AutomationModes.IsValid(requested)) return Block(AutomationReasons.InvalidMode);
        var effective = requested;
        var reason = AutomationReasons.GlobalMode;
        if (categoryCode == AutomationCategories.Application)
        {
            effective = AutomationModes.Manual;
            reason = AutomationReasons.ApplicationCap;
        }
        else if (categoryCode == AutomationCategories.Email && requested == AutomationModes.Automatic)
        {
            effective = AutomationModes.Assist;
            reason = AutomationReasons.EmailCap;
        }
        var decision = effective switch
        {
            AutomationModes.Manual => AutomationDecisions.Manual,
            AutomationModes.Assist => AutomationDecisions.ApprovalRequired,
            _ => AutomationDecisions.Automatic
        };
        return new(categoryCode, requested, effective, decision, reason);
    }
}
