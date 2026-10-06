using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Ingestions;
using ProspectionCrm.Api.Dtos.SourceExecutions;
using ProspectionCrm.Api.Entities;
using static ProspectionCrm.Api.Services.IngestionHistoryCodes;

namespace ProspectionCrm.Api.Services;

public sealed class IngestionService(ProspectionCrmDbContext dbContext,
    ICurrentWorkspaceProvider workspaceProvider, ILogger<IngestionService> logger) : IIngestionService
{
    private const string BusinessSavepoint = "ingestion_business";

    public async Task<IngestionResult> IngestAsync(Guid savedSearchId, IngestionRequest request, CancellationToken cancellationToken)
    {
        var invalid = Validate(request);
        if (invalid is not null) return new(IngestionStatus.InvalidRequest, Error: invalid);
        Guid workspaceId;
        try { workspaceId = await workspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken); }
        catch (InvalidOperationException)
        {
            return Reject(IngestionStatus.Conflict, IngestionErrorCode.WorkspaceUnavailable, "Exactly one active workspace is required.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Identity writers of the SAME workspace serialize. No global or table lock, and no
        // workspace NO KEY UPDATE lock that would unnecessarily block Phase 4 default/archive.
        await WorkspaceIdentityLock.AcquireAsync(dbContext, workspaceId, cancellationToken);

        // Consistent order: ingestion advisory lock, workspace, search, configuration, pipeline.
        // KEY SHARE protects ownership/FKs; SHARE freezes selected configuration and destination.
        var workspace = await dbContext.Workspaces.FromSqlInterpolated(
            $"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR KEY SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (workspace is null || workspace.ArchivedAt is not null)
            return Reject(IngestionStatus.Conflict, IngestionErrorCode.WorkspaceUnavailable, "The workspace is no longer active.");
        var search = await dbContext.SavedSearches.FromSqlInterpolated(
            $"SELECT * FROM \"SavedSearches\" WHERE \"Id\" = {savedSearchId} AND \"WorkspaceId\" = {workspaceId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (search is null) return NotFound();
        var configuration = await dbContext.SourceConfigurations.FromSqlInterpolated(
            $"SELECT * FROM \"SourceConfigurations\" WHERE \"Id\" = {search.SourceConfigurationId} AND \"WorkspaceId\" = {workspaceId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var pipeline = await dbContext.Pipelines.FromSqlInterpolated(
            $"SELECT * FROM \"Pipelines\" WHERE \"Id\" = {search.PipelineId} AND \"WorkspaceId\" = {workspaceId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (configuration is null || pipeline is null) return NotFound();
        var stage = await dbContext.PipelineStages.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.PipelineStageId && x.Pipeline.WorkspaceId == workspaceId, cancellationToken);
        if (stage is null) return NotFound();
        if (stage.PipelineId != pipeline.Id)
            return Reject(IngestionStatus.Conflict, IngestionErrorCode.WrongPipeline, "The target stage must belong to the saved search pipeline.");
        if (!search.Enabled || search.ArchivedAt is not null || !configuration.Enabled || configuration.ArchivedAt is not null
            || pipeline.ArchivedAt is not null || stage.ArchivedAt is not null)
            return Reject(IngestionStatus.Conflict, IngestionErrorCode.InactiveResource, "The search and configuration must be enabled; the search, configuration, pipeline and stage must not be archived.");

        var execution = new SourceExecution
        {
            WorkspaceId = workspaceId, SourceConfigurationId = configuration.Id, SavedSearchId = search.Id,
            TriggerTypeCode = "manual", StatusCode = "running", ItemsFound = request.Items!.Count,
            HistoryVersion = 1, ContractVersion = 1, NormalizationVersion = 1,
            TargetPipelineId = pipeline.Id, TargetPipelineStageId = stage.Id,
            ContextSnapshotJson = IngestionHistory.Context(configuration, search, pipeline, stage)
        };
        var history = request.Items.Select((item, index) => IngestionHistory.Input(execution, item, index)).ToArray();
        execution.Items = history.ToList();
        dbContext.SourceExecutions.Add(execution);
        await dbContext.SaveChangesAsync(cancellationToken);
        // Keep the immutable inputs on rollback; business writes and provisional decisions follow.
        await transaction.CreateSavepointAsync(BusinessSavepoint, cancellationToken);
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["WorkspaceId"] = workspaceId, ["SavedSearchId"] = search.Id, ["SourceExecutionId"] = execution.Id
        });
        logger.LogInformation("Manual ingestion started with {ItemCount} items", execution.ItemsFound);
        var provisional = new Dictionary<int, IngestionHistory.Provisional>();
        var currentIndex = 0;
        try
        {
            // Include archives. Source identities use persisted keys; current business text
            // uses the unchanged V1 comparison and history uses its normalized index.
            var opportunities = await dbContext.Opportunities.AsNoTracking().Include(x => x.Company)
                .Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.Id).ToListAsync(cancellationToken);
            var sources = await dbContext.OpportunitySources
                .Where(x => x.WorkspaceId == workspaceId).OrderBy(x => x.Id).ToListAsync(cancellationToken);
            var batchMatches = new Dictionary<(string Title, string Company), HashSet<Guid>>();
            var seen = new Dictionary<(string?, string?, string, string?), int>();
            var outcomes = new List<IngestionItemDto>();
            for (var index = 0; index < request.Items.Count; index++)
            {
                currentIndex = index;
                cancellationToken.ThrowIfCancellationRequested();
                var item = request.Items[index];
                var url = IngestionNormalization.UrlKey(item.SourceUrl);
                var title = IngestionNormalization.TextKey(item.Title);
                var company = item.CompanyName is null ? null : IngestionNormalization.TextKey(item.CompanyName);
                var externalMatches = item.ExternalId is null ? [] : sources
                    .Where(x => x.SourceConfigurationId == configuration.Id && x.ExternalId == item.ExternalId)
                    .Select(x => x.OpportunityId).Distinct().ToArray();
                var urlMatches = url is null ? [] : sources
                    .Where(x => x.NormalizedSourceUrl == url)
                    .Select(x => x.OpportunityId).Distinct().ToArray();
                var currentTextMatches = company is null ? [] : opportunities
                    .Where(x => x.Company?.WorkspaceId == workspaceId
                        && IngestionNormalization.TextKey(x.Title) == title
                        && IngestionNormalization.TextKey(x.Company.Name) == company)
                    .Select(x => x.Id).Distinct().ToArray();
                var historicalMatches = company is null ? [] : await dbContext.SourceExecutionItems.AsNoTracking()
                    .Where(x => x.WorkspaceId == workspaceId && x.NormalizedTitle == title && x.NormalizedCompanyName == company
                        && x.OpportunityId != null && x.Opportunity!.WorkspaceId == workspaceId
                        && (x.OutcomeCode == Outcomes.Created || x.OutcomeCode == Outcomes.Updated || x.OutcomeCode == Outcomes.Ignored))
                    .Select(x => x.OpportunityId!.Value).Distinct().ToArrayAsync(cancellationToken);
                var inBatch = company is not null && batchMatches.TryGetValue((title, company), out var ids) ? ids : [];
                var textMatches = currentTextMatches.Concat(historicalMatches).Concat(inBatch).Distinct().ToArray();
                // Precedence never hides a contradictory weaker identity or an ambiguous key.
                var candidates = externalMatches.Concat(urlMatches).Concat(textMatches).Distinct().ToArray();
                if (candidates.Length > 1)
                    throw new IdentityConflict(IngestionErrorCode.AmbiguousIdentity,
                        "The supplied identities match multiple opportunities. No automatic merge was performed.", index,
                        candidates.Order().ToArray());
                var opportunityId = externalMatches.Cast<Guid?>().FirstOrDefault()
                    ?? urlMatches.Cast<Guid?>().FirstOrDefault() ?? textMatches.Cast<Guid?>().FirstOrDefault();
                var created = opportunityId is null;
                if (created)
                {
                    // There is no free-text company field. Do not invent a Company, assign one
                    // by a non-unique name, or hide identity data in Notes/JSON intended for another purpose.
                    if (item.ExternalId is null && url is null)
                        throw new IdentityConflict(IngestionErrorCode.MissingPersistentIdentity,
                            "No existing title/company match was found. A new opportunity requires ExternalId or SourceUrl.", index);
                    var opportunity = new Opportunity
                    {
                        WorkspaceId = workspaceId, PipelineStageId = stage.Id, Title = item.Title,
                        PriorityCode = "normal", Location = item.Location, Notes = item.Description
                    };
                    dbContext.Opportunities.Add(opportunity);
                    opportunities.Add(opportunity);
                    opportunityId = opportunity.Id;
                }

                var signature = (item.ExternalId, url, title, company);
                var observation = history[index];
                if (seen.TryGetValue(signature, out var firstIndex))
                {
                    execution.ItemsIgnored++;
                    IngestionHistory.Decide(observation, Outcomes.Ignored, DuplicateInBatch, opportunityId!.Value,
                        new { duplicateOfItemIndex = firstIndex });
                    // No provenance observation or links for an internal duplicate.
                    await dbContext.SaveChangesAsync(cancellationToken);
                    provisional.Add(index, new(Outcomes.Ignored, opportunityId, firstIndex));
                    Remember(title, company, opportunityId.Value);
                    outcomes.Add(new(index, opportunityId.Value, Outcomes.Ignored));
                    currentIndex = index + 1;
                    continue;
                }
                seen.Add(signature, index);
                var usedSources = Observe(sources, opportunityId!.Value, configuration, search.Id, execution, item.ExternalId, url);
                foreach (var (source, role) in usedSources.DistinctBy(x => (x.Source.Id, x.Role)))
                    dbContext.SourceExecutionItemSources.Add(new SourceExecutionItemSource
                    {
                        WorkspaceId = workspaceId, SourceExecutionItemId = observation.Id,
                        OpportunitySourceId = source.Id, OpportunitySourceIdSnapshot = source.Id, RoleCode = role
                    });
                var matches = new List<string>();
                if (externalMatches.Length > 0) matches.Add(Identities.ExternalId);
                if (urlMatches.Length > 0) matches.Add(Identities.SourceUrl);
                if (textMatches.Length > 0) matches.Add(Identities.TitleCompany);
                var provided = new List<string>();
                if (item.ExternalId is not null) provided.Add(Identities.ExternalId);
                if (url is not null) provided.Add(Identities.SourceUrl);
                IngestionHistory.Decide(observation, created ? Outcomes.Created : Outcomes.Updated,
                    created ? CreatedNewOpportunity : IngestionHistory.MatchDecision(matches), opportunityId.Value,
                    created ? (object)new { providedIdentities = provided } : new { matchedBy = matches });
                if (created) execution.ItemsCreated++;
                else execution.ItemsUpdated++;
                // One opportunity and its provenance are always saved together, in our transaction.
                await dbContext.SaveChangesAsync(cancellationToken);
                provisional.Add(index, new(observation.OutcomeCode, opportunityId, null));
                Remember(title, company, opportunityId.Value);
                outcomes.Add(new(index, opportunityId.Value, observation.OutcomeCode));
                currentIndex = index + 1;
            }
            void Remember(string title, string? company, Guid id)
            {
                if (company is null) return;
                if (!batchMatches.TryGetValue((title, company), out var ids))
                    batchMatches[(title, company)] = ids = [];
                ids.Add(id);
            }
            execution.StatusCode = "succeeded";
            execution.FinishedAt = Later(DateTimeOffset.UtcNow, execution.StartedAt);
            await dbContext.SaveChangesAsync(cancellationToken);
            // Return the persisted timestamp precision, identical to GET /source-executions.
            await dbContext.Entry(execution).ReloadAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Manual ingestion succeeded: {Created} created, {Updated} rediscovered, {Ignored} ignored",
                execution.ItemsCreated, execution.ItemsUpdated, execution.ItemsIgnored);
            return new(IngestionStatus.Succeeded, new(ToDto(execution), outcomes));
        }
        catch (IdentityConflict conflict)
        {
            var decision = conflict.Code == IngestionErrorCode.AmbiguousIdentity ? AmbiguousIdentity : MissingPersistentIdentity;
            await FinishFailedAsync(transaction, execution, "failed", decision, conflict.Index, provisional,
                conflict.Candidates is null ? null : JsonSerializer.Serialize(new { candidateOpportunityIds = conflict.Candidates }));
            return new(IngestionStatus.Conflict, Error: new(conflict.Code, conflict.Message, conflict.Index, execution.Id));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinishFailedAsync(transaction, execution, "cancelled", RequestCancelled, currentIndex, provisional);
            throw;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "UX_OpportunitySources_Workspace_SourceConfiguration_ExternalId" or "UX_OpportunitySources_Workspace_NormalizedSourceUrl"
        })
        {
            await FinishFailedAsync(transaction, execution, "failed", ConcurrentIdentityChange, currentIndex, provisional);
            return new(IngestionStatus.Conflict, Error: new(IngestionErrorCode.ConcurrentIdentityChange,
                "A source identity was concurrently modified. The entire lot was rolled back.", ExecutionId: execution.Id));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Manual ingestion failed; rolling back business writes");
            await FinishFailedAsync(transaction, execution, "failed", PersistenceFailure, currentIndex, provisional);
            return new(IngestionStatus.Failed, Error: new(IngestionErrorCode.PersistenceFailure,
                "The ingestion could not be persisted. The entire lot was rolled back.", ExecutionId: execution.Id));
        }
    }

    private IReadOnlyList<(OpportunitySource Source, string Role)> Observe(List<OpportunitySource> sources, Guid opportunityId, SourceConfiguration configuration,
        Guid searchId, SourceExecution execution, string? externalId, string? url)
    {
        if (externalId is null && url is null) return [];
        OpportunitySource Add(string? external, string? sourceUrl)
        {
            var source = new OpportunitySource
            {
                WorkspaceId = execution.WorkspaceId, OpportunityId = opportunityId, SourceConfigurationId = configuration.Id, SavedSearchId = searchId,
                NormalizedSourceUrl = sourceUrl,
                SourceExecutionId = execution.Id, SourceLabel = configuration.Name, ExternalId = external, SourceUrl = sourceUrl,
                FirstSeenAt = execution.StartedAt, LastSeenAt = execution.StartedAt
            };
            sources.Add(source);
            dbContext.OpportunitySources.Add(source);
            return source;
        }
        var externalSource = externalId is null ? null : sources.SingleOrDefault(x =>
            x.SourceConfigurationId == configuration.Id && x.ExternalId == externalId);
        var urlSources = url is null ? [] : sources.Where(x => x.OpportunityId == opportunityId
            && x.NormalizedSourceUrl == url).ToArray();
        if (externalId is not null && externalSource is null)
        {
            // A URL already attached by another source cannot be duplicated under the current
            // SQL index. Keep its origin and store the new external identity separately.
            externalSource = Add(externalId, urlSources.Length == 0 ? url : null);
        }
        if (url is not null && urlSources.Length == 0 && externalSource?.SourceUrl != url)
            urlSources = [Add(null, url)];
        foreach (var source in urlSources.Concat(externalSource is null ? [] : new[] { externalSource }).Distinct())
            source.LastSeenAt = Later(execution.StartedAt, Later(source.FirstSeenAt, source.LastSeenAt ?? source.FirstSeenAt));
        // Never replace existing provenance identifiers, URLs, origin search/execution or label.
        var used = new List<(OpportunitySource Source, string Role)>();
        if (externalId is not null && externalSource is not null) used.Add((externalSource, Identities.ExternalId));
        if (url is not null)
        {
            foreach (var source in sources.Where(x => x.OpportunityId == opportunityId
                && x.NormalizedSourceUrl == url))
                used.Add((source, Identities.SourceUrl));
        }
        return used;
    }

    private async Task FinishFailedAsync(IDbContextTransaction transaction, SourceExecution execution, string status,
        string error, int currentIndex, IReadOnlyDictionary<int, IngestionHistory.Provisional> provisional,
        string? rejectionDetails = null)
    {
        // Cleanup must not inherit a cancelled HTTP token. Bound it, with no automatic retry.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await transaction.RollbackToSavepointAsync(BusinessSavepoint, cleanup.Token);
        dbContext.ChangeTracker.Clear();
        var finished = Later(DateTimeOffset.UtcNow, execution.StartedAt);
        var persisted = await dbContext.SourceExecutions.Include(x => x.Items)
            .SingleAsync(x => x.Id == execution.Id, cleanup.Token);
        persisted.StatusCode = status;
        persisted.FinishedAt = finished;
        persisted.ErrorMessage = error;
        persisted.ItemsCreated = persisted.ItemsUpdated = persisted.ItemsIgnored = 0;
        foreach (var item in persisted.Items.OrderBy(x => x.ItemIndex))
        {
            item.ProcessedAt = Later(finished, item.ReceivedAt);
            item.OpportunityId = item.OpportunityIdSnapshot = null;
            if (provisional.TryGetValue(item.ItemIndex, out var previous))
            {
                item.OutcomeCode = Outcomes.RolledBack;
                item.DecisionCode = RolledBackAfterFailure;
                item.DecisionDetailsJson = JsonSerializer.Serialize(new
                {
                    provisionalOutcomeCode = previous.OutcomeCode, provisionalOpportunityId = previous.OpportunityId
                });
                persisted.ItemsRolledBack++;
            }
            else if (item.ItemIndex == currentIndex)
            {
                var cancelled = status == "cancelled";
                item.OutcomeCode = cancelled ? Outcomes.Cancelled : Outcomes.Rejected;
                item.DecisionCode = error;
                item.DecisionDetailsJson = cancelled
                    ? JsonSerializer.Serialize(new { cancelledAtItemIndex = currentIndex }) : rejectionDetails;
                if (cancelled) persisted.ItemsCancelled++;
                else persisted.ItemsRejected++;
            }
            else
            {
                item.OutcomeCode = Outcomes.NotProcessed;
                item.DecisionCode = NotProcessedAfterFailure;
                item.DecisionDetailsJson = JsonSerializer.Serialize(new { blockedByItemIndex = currentIndex });
                persisted.ItemsNotProcessed++;
            }
        }
        await dbContext.SaveChangesAsync(cleanup.Token);
        await transaction.CommitAsync(cleanup.Token);
        logger.LogWarning("Manual ingestion ended with {Status} and {ErrorCode}; all {ItemCount} business decisions rolled back",
            status, error, execution.ItemsFound);
    }

    private static IngestionError? Validate(IngestionRequest request)
    {
        if (request.PipelineStageId is null || request.PipelineStageId == Guid.Empty
            || request.Items is null || request.Items.Count is < 1 or > 100)
            return new(IngestionErrorCode.InvalidBatch, "PipelineStageId and a lot of 1 to 100 items are required.");
        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            if (item is null || !Validator.TryValidateObject(item, new ValidationContext(item), null, true)
                || (item.ExternalId is not null && string.IsNullOrWhiteSpace(item.ExternalId))
                || (item.CompanyName is not null && string.IsNullOrWhiteSpace(item.CompanyName))
                || (item.SourceUrl is not null && IngestionNormalization.UrlKey(item.SourceUrl) is null)
                || (item.ExternalId is null && item.SourceUrl is null && item.CompanyName is null))
                return new(IngestionErrorCode.InvalidBatch,
                    "Each item needs a title and ExternalId, an absolute HTTP(S) URL without credentials, or CompanyName for an existing title/company match; field lengths must be valid.", i);
        }
        return null;
    }

    private static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;
    private static IngestionResult NotFound() => Reject(IngestionStatus.NotFound, IngestionErrorCode.ResourceNotFound,
        "The saved search, configuration, pipeline or target stage does not exist in the current workspace.");
    private static IngestionResult Reject(IngestionStatus status, IngestionErrorCode code, string detail) => new(status, Error: new(code, detail));
    private sealed class IdentityConflict(IngestionErrorCode code, string message, int index, Guid[]? candidates = null) : Exception(message)
    {
        internal IngestionErrorCode Code { get; } = code;
        internal int Index { get; } = index;
        internal Guid[]? Candidates { get; } = candidates;
    }
    private static SourceExecutionDto ToDto(SourceExecution execution) => new()
    {
        Id = execution.Id, SourceConfigurationId = execution.SourceConfigurationId, SavedSearchId = execution.SavedSearchId,
        TriggerTypeCode = execution.TriggerTypeCode, StatusCode = execution.StatusCode,
        StartedAt = execution.StartedAt, FinishedAt = execution.FinishedAt, ItemsFound = execution.ItemsFound,
        ItemsCreated = execution.ItemsCreated, ItemsUpdated = execution.ItemsUpdated, ItemsIgnored = execution.ItemsIgnored,
        ErrorMessage = execution.ErrorMessage,
        HistoryAvailable = execution.HistoryVersion.HasValue,
        HistoryVersion = execution.HistoryVersion,
        ContractVersion = execution.ContractVersion,
        NormalizationVersion = execution.NormalizationVersion,
        TargetPipelineId = execution.TargetPipelineId,
        TargetPipelineStageId = execution.TargetPipelineStageId,
        ItemsRejected = execution.ItemsRejected,
        ItemsRolledBack = execution.ItemsRolledBack,
        ItemsNotProcessed = execution.ItemsNotProcessed,
        ItemsCancelled = execution.ItemsCancelled,
    };
}
