namespace ProspectionCrm.Blazor.Helpers;

using ProspectionCrm.Blazor.Models;

public static class FollowUpActionHelper
{
    public static bool IsOverdue(FollowUpActionDto action)
    {
        return !action.IsCompleted
            && action.DueDate.Date < DateTime.Today;
    }

    public static bool IsDueToday(FollowUpActionDto action)
    {
        return !action.IsCompleted
            && action.DueDate.Date == DateTime.Today;
    }
}