using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.IngestionHistory;
using ProspectionCrm.Api.Dtos.SourceExecutions;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public sealed class IngestionHistoryReadService(ProspectionCrmDbContext db, ICurrentWorkspaceProvider workspaceProvider)
    : IIngestionHistoryReadService
{
    public async Task<IngestionHistoryReadResult<SourceExecutionHistoryDto>> GetExecutionHistoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspace = await workspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var row = await db.SourceExecutions.AsNoTracking().Where(x => x.WorkspaceId == workspace && x.Id == id)
            .Select(x => new
            {
                Execution = new SourceExecutionDto
                {
                    Id = x.Id, SourceConfigurationId = x.SourceConfigurationId, SavedSearchId = x.SavedSearchId,
                    TriggerTypeCode = x.TriggerTypeCode, StatusCode = x.StatusCode, StartedAt = x.StartedAt, FinishedAt = x.FinishedAt,
                    ItemsFound = x.ItemsFound, ItemsCreated = x.ItemsCreated, ItemsUpdated = x.ItemsUpdated, ItemsIgnored = x.ItemsIgnored,
                    ErrorMessage = x.ErrorMessage, HistoryAvailable = x.HistoryVersion.HasValue, HistoryVersion = x.HistoryVersion,
                    ContractVersion = x.ContractVersion, NormalizationVersion = x.NormalizationVersion,
                    TargetPipelineId = x.TargetPipelineId, TargetPipelineStageId = x.TargetPipelineStageId,
                    ItemsRejected = x.ItemsRejected, ItemsRolledBack = x.ItemsRolledBack,
                    ItemsNotProcessed = x.ItemsNotProcessed, ItemsCancelled = x.ItemsCancelled
                },
                x.ContextSnapshotJson
            }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return Missing<SourceExecutionHistoryDto>();
        return Success(new SourceExecutionHistoryDto(row.Execution,
            row.Execution.HistoryAvailable ? HistoricalJsonReader.Read(row.ContextSnapshotJson) : null,
            row.Execution.HistoryAvailable ? null : "legacy-execution"));
    }

    public async Task<IngestionHistoryReadResult<SourceExecutionItemsPageDto>> GetExecutionItemsAsync(
        Guid id, ExecutionItemsQuery query, CancellationToken cancellationToken)
    {
        if (Validate(query) is { } error) return Invalid<SourceExecutionItemsPageDto>(error);
        var workspace = await workspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var execution = await db.SourceExecutions.AsNoTracking().Where(x => x.WorkspaceId == workspace && x.Id == id)
            .Select(x => new { x.HistoryVersion }).SingleOrDefaultAsync(cancellationToken);
        if (execution is null) return Missing<SourceExecutionItemsPageDto>();
        if (!execution.HistoryVersion.HasValue)
            return Success(new SourceExecutionItemsPageDto(query.Offset, query.Limit, 0, false, [], id, false));
        var items = Filter(Items(workspace).Where(x => x.SourceExecutionId == id), query);
        var page = await ReadPageAsync(items, workspace, query, executionOrder: true, cancellationToken);
        return Success(new SourceExecutionItemsPageDto(query.Offset, query.Limit, page.TotalCount,
            HasMore(query, page), page.Items, id, true));
    }

    public async Task<IngestionHistoryReadResult<OpportunityObservationsPageDto>> GetOpportunityObservationsAsync(
        Guid id, OpportunityObservationsQuery query, CancellationToken cancellationToken)
    {
        if (Validate(query) is { } error) return Invalid<OpportunityObservationsPageDto>(error);
        var workspace = await workspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await db.Opportunities.AsNoTracking().AnyAsync(x => x.WorkspaceId == workspace && x.Id == id, cancellationToken))
            return Missing<OpportunityObservationsPageDto>();
        var items = Filter(Items(workspace).Where(x => x.OpportunityId == id), query);
        if (query.SourceConfigurationId is { } configuration)
            items = items.Where(x => x.SourceExecution.SourceConfigurationId == configuration
                && x.SourceExecution.SourceConfiguration.WorkspaceId == workspace);
        if (query.SavedSearchId is { } search)
            items = items.Where(x => x.SourceExecution.SavedSearchId == search
                && x.SourceExecution.SavedSearch!.WorkspaceId == workspace);
        var page = await ReadPageAsync(items, workspace, query, executionOrder: false, cancellationToken);
        return Success(new OpportunityObservationsPageDto(query.Offset, query.Limit, page.TotalCount,
            HasMore(query, page), page.Items, id));
    }

    public async Task<IngestionHistoryReadResult<OpportunitySourceObservationsPageDto>> GetSourceObservationsAsync(
        Guid opportunityId, Guid sourceId, OpportunitySourceObservationsQuery query, CancellationToken cancellationToken)
    {
        if (Validate(query) is { } error) return Invalid<OpportunitySourceObservationsPageDto>(error);
        var workspace = await workspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await db.OpportunitySources.AsNoTracking().AnyAsync(x => x.WorkspaceId == workspace && x.Id == sourceId
                && x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspace, cancellationToken))
            return Missing<OpportunitySourceObservationsPageDto>();
        // EXISTS is a SQL semijoin: each item is counted/paged once even with two matching roles.
        var items = Filter(Items(workspace).Where(x => x.Sources.Any(s => s.WorkspaceId == workspace
            && s.OpportunitySourceId == sourceId && (query.RoleCode == null || s.RoleCode == query.RoleCode))), query);
        var page = await ReadPageAsync(items, workspace, query, executionOrder: false, cancellationToken);
        return Success(new OpportunitySourceObservationsPageDto(query.Offset, query.Limit, page.TotalCount,
            HasMore(query, page), page.Items, opportunityId, sourceId));
    }

    private IQueryable<SourceExecutionItem> Items(Guid workspace) => db.SourceExecutionItems.AsNoTracking()
        .Where(x => x.WorkspaceId == workspace && x.SourceExecution.WorkspaceId == workspace);

    private static IQueryable<SourceExecutionItem> Filter(IQueryable<SourceExecutionItem> items, ExecutionItemsQuery query)
    {
        if (query.OutcomeCode is { } outcome) items = items.Where(x => x.OutcomeCode == outcome);
        if (query.DecisionCode is { } decision) items = items.Where(x => x.DecisionCode == decision);
        if (query is ObservationQuery observation)
        {
            if (observation.From is { } from)
            {
                var utc = from.ToUniversalTime(); items = items.Where(x => x.ReceivedAt >= utc);
            }
            if (observation.To is { } to)
            {
                var utc = to.ToUniversalTime(); items = items.Where(x => x.ReceivedAt <= utc);
            }
        }
        return items;
    }

    private sealed record Page(int TotalCount, IReadOnlyList<SourceExecutionItemDto> Items);
    private static bool HasMore(ExecutionItemsQuery query, Page page) => (long)query.Offset + page.Items.Count < page.TotalCount;

    private async Task<Page> ReadPageAsync(IQueryable<SourceExecutionItem> items, Guid workspace,
        ExecutionItemsQuery query, bool executionOrder, CancellationToken cancellationToken)
    {
        var total = await items.CountAsync(cancellationToken);
        var ordered = executionOrder ? items.OrderBy(x => x.ItemIndex).ThenBy(x => x.Id)
            : items.OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.Id);
        var rows = await ordered.Skip(query.Offset).Take(query.Limit).Select(x => new
        {
            Item = new SourceExecutionItemDto
            {
                Id = x.Id, SourceExecutionId = x.SourceExecutionId,
                SourceConfigurationId = x.SourceExecution.SourceConfigurationId, SavedSearchId = x.SourceExecution.SavedSearchId,
                TriggerTypeCode = x.SourceExecution.TriggerTypeCode, ExecutionStatusCode = x.SourceExecution.StatusCode,
                ExecutionStartedAt = x.SourceExecution.StartedAt, TargetPipelineId = x.SourceExecution.TargetPipelineId,
                TargetPipelineStageId = x.SourceExecution.TargetPipelineStageId,
                ItemIndex = x.ItemIndex, OpportunityId = x.OpportunityId, OpportunityIdSnapshot = x.OpportunityIdSnapshot,
                OutcomeCode = x.OutcomeCode, DecisionCode = x.DecisionCode, ReceivedAt = x.ReceivedAt, ProcessedAt = x.ProcessedAt,
                Title = x.Title, NormalizedTitle = x.NormalizedTitle, CompanyName = x.CompanyName, NormalizedCompanyName = x.NormalizedCompanyName,
                ExternalId = x.ExternalId, SourceUrl = x.SourceUrl, NormalizedSourceUrl = x.NormalizedSourceUrl
            },
            x.PayloadSnapshotJson, x.DecisionDetailsJson
        }).ToListAsync(cancellationToken);
        if (rows.Count == 0) return new(total, []);
        var ids = rows.Select(x => x.Item.Id).ToArray();
        var links = await db.SourceExecutionItemSources.AsNoTracking()
            .Where(x => x.WorkspaceId == workspace && ids.Contains(x.SourceExecutionItemId))
            .OrderBy(x => x.RoleCode).ThenBy(x => x.OpportunitySourceIdSnapshot).ThenBy(x => x.Id)
            .Select(x => new { x.SourceExecutionItemId, Source = new SourceExecutionItemSourceDto(x.Id,
                x.OpportunitySourceId, x.OpportunitySourceIdSnapshot, x.RoleCode) }).ToListAsync(cancellationToken);
        var byItem = links.ToLookup(x => x.SourceExecutionItemId, x => x.Source);
        foreach (var row in rows)
        {
            row.Item.PayloadSnapshot = HistoricalJsonReader.Read(row.PayloadSnapshotJson);
            row.Item.DecisionDetails = HistoricalJsonReader.Read(row.DecisionDetailsJson);
            row.Item.Sources = byItem[row.Item.Id].ToArray();
        }
        return new(total, rows.Select(x => x.Item).ToArray());
    }

    private sealed record Error(IngestionHistoryReadErrorCode Code, string Detail);
    private static Error? Validate(ExecutionItemsQuery query)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 200)
            return new(IngestionHistoryReadErrorCode.InvalidPagination, "Offset must be nonnegative and limit between 1 and 200.");
        if (query.OutcomeCode is not null && query.OutcomeCode is not
            ("created" or "updated" or "ignored" or "rejected" or "rolled-back" or "not-processed" or "cancelled" or "pending"))
            return new(IngestionHistoryReadErrorCode.InvalidOutcomeCode, "The outcome code is not supported.");
        if (query.DecisionCode is not null && (string.IsNullOrWhiteSpace(query.DecisionCode) || query.DecisionCode.Length > 100))
            return new(IngestionHistoryReadErrorCode.InvalidDecisionCode, "Decision code must be nonblank and at most 100 characters.");
        if (query is OpportunitySourceObservationsQuery { RoleCode: not null } source && source.RoleCode is not ("external-id" or "source-url"))
            return new(IngestionHistoryReadErrorCode.InvalidRoleCode, "Role code must be external-id or source-url.");
        if (query is ObservationQuery dates && dates.From > dates.To)
            return new(IngestionHistoryReadErrorCode.InvalidDateRange, "From must not be after to.");
        return null;
    }

    private static IngestionHistoryReadResult<T> Success<T>(T value) => new(IngestionHistoryReadStatus.Succeeded, value);
    private static IngestionHistoryReadResult<T> Missing<T>() => new(IngestionHistoryReadStatus.NotFound,
        Code: IngestionHistoryReadErrorCode.HistoryResourceNotFound, Detail: "The requested resource does not exist in the current workspace and scope.");
    private static IngestionHistoryReadResult<T> Invalid<T>(Error error) => new(IngestionHistoryReadStatus.InvalidRequest, Code: error.Code, Detail: error.Detail);
}
