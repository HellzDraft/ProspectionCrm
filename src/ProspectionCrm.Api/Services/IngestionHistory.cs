using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProspectionCrm.Api.Dtos.Ingestions;
using ProspectionCrm.Api.Entities;
using static ProspectionCrm.Api.Services.IngestionHistoryCodes;

namespace ProspectionCrm.Api.Services;

internal static class IngestionHistory
{
    internal static string Context(SourceConfiguration source, SavedSearch search, Pipeline pipeline, PipelineStage stage)
    {
        using var criteria = JsonDocument.Parse(search.CriteriaJson);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            sourceConfiguration = new
            {
                id = source.Id, name = source.Name, sourceTypeCode = source.SourceTypeCode, baseUrl = source.BaseUrl,
                configurationSha256 = source.ConfigurationJson is null ? null
                    : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.ConfigurationJson)))
            },
            savedSearch = new { id = search.Id, name = search.Name, searchUrl = search.SearchUrl, criteria = criteria.RootElement },
            pipeline = new { id = pipeline.Id, name = pipeline.Name, typeCode = pipeline.TypeCode },
            targetStage = new { id = stage.Id, name = stage.Name, categoryCode = stage.CategoryCode }
        });
    }

    internal static SourceExecutionItem Input(SourceExecution execution, IngestionItemRequest item, int index) => new()
    {
        WorkspaceId = execution.WorkspaceId, SourceExecutionId = execution.Id, ItemIndex = index,
        OutcomeCode = Outcomes.Pending, DecisionCode = Pending, ReceivedAt = execution.StartedAt,
        Title = item.Title, NormalizedTitle = IngestionNormalization.TextKey(item.Title),
        CompanyName = item.CompanyName,
        NormalizedCompanyName = item.CompanyName is null ? null : IngestionNormalization.TextKey(item.CompanyName),
        ExternalId = item.ExternalId, SourceUrl = item.SourceUrl, NormalizedSourceUrl = IngestionNormalization.UrlKey(item.SourceUrl),
        PayloadSnapshotJson = item.Location is null && item.Description is null ? null
            : JsonSerializer.Serialize(new { location = item.Location, description = item.Description })
    };

    internal static void Decide(SourceExecutionItem item, string outcome, string decision, Guid opportunityId, object details)
    {
        item.OutcomeCode = outcome;
        item.DecisionCode = decision;
        item.OpportunityId = opportunityId;
        item.OpportunityIdSnapshot = opportunityId;
        var now = DateTimeOffset.UtcNow;
        item.ProcessedAt = now > item.ReceivedAt ? now : item.ReceivedAt;
        item.DecisionDetailsJson = JsonSerializer.Serialize(details);
    }

    internal static string MatchDecision(IReadOnlyList<string> matches) => matches.Count > 1
        ? MatchedConsistentIdentities : matches.Single() switch
        {
            Identities.ExternalId => MatchedExternalId,
            Identities.SourceUrl => MatchedSourceUrl,
            Identities.TitleCompany => MatchedTitleCompany,
            _ => throw new InvalidOperationException("Unknown controlled identity.")
        };

    internal sealed record Provisional(string OutcomeCode, Guid? OpportunityId, int? DuplicateOfItemIndex);
}
