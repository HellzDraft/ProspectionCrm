using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.IngestionHistory;

public class ExecutionItemsQuery
{
    public int Offset { get; set; }
    public int Limit { get; set; } = 50;
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? OutcomeCode { get; set; }
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? DecisionCode { get; set; }
}

public class ObservationQuery : ExecutionItemsQuery
{
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
}

public sealed class OpportunityObservationsQuery : ObservationQuery
{
    public Guid? SourceConfigurationId { get; set; }
    public Guid? SavedSearchId { get; set; }
}

public sealed class OpportunitySourceObservationsQuery : ObservationQuery
{
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? RoleCode { get; set; }
}
