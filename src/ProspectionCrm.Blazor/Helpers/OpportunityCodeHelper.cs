using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Helpers;

public static class OpportunityCodeHelper
{
    public static string GetPriorityCode(OpportunityPriority priority) => priority.ToString().ToLowerInvariant();

    public static OpportunityPriority? GetPriority(string code) => code switch
    {
        "low" => OpportunityPriority.Low,
        "normal" => OpportunityPriority.Normal,
        "high" => OpportunityPriority.High,
        _ => null
    };

    public static string GetPriorityLabel(string code) => GetPriority(code) is { } value
        ? OpportunityDisplayHelper.GetPriorityLabel(value) : code;

    public static string GetCompanyLabel(Guid? companyId) =>
        companyId.HasValue ? $"ID entreprise : {companyId.Value}" : "—";
}
