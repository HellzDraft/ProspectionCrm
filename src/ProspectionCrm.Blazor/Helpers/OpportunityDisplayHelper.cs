using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Helpers;

public static class OpportunityDisplayHelper
{
    public static string GetPriorityLabel(OpportunityPriority priority) => priority switch
    {
        OpportunityPriority.Low => "Faible",
        OpportunityPriority.Normal => "Normale",
        OpportunityPriority.High => "Haute",
        _ => "Inconnue"
    };
}
