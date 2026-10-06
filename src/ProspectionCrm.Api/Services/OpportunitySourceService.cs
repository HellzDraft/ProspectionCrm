using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.OpportunitySources;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class OpportunitySourceService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : IOpportunitySourceService
{
    public async Task<IReadOnlyList<OpportunitySourceDto>?> GetAllAsync(Guid opportunityId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.Opportunities.AnyAsync(x => x.Id == opportunityId && x.WorkspaceId == workspaceId, cancellationToken))
            return null;
        return (await dbContext.OpportunitySources.AsNoTracking()
            .Where(x => x.OpportunityId == opportunityId && x.WorkspaceId == workspaceId)
            .OrderBy(x => x.FirstSeenAt).ThenBy(x => x.Id).ToListAsync(cancellationToken)).Select(ToDto).ToList();
    }

    public async Task<OpportunitySourceDto?> GetByIdAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var source = await dbContext.OpportunitySources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id
            && x.OpportunityId == opportunityId && x.WorkspaceId == workspaceId, cancellationToken);
        return source is null ? null : ToDto(source);
    }

    public async Task<OpportunitySourceWriteResult> CreateAsync(Guid opportunityId,
        CreateOpportunitySourceRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await WorkspaceIdentityLock.AcquireAsync(dbContext, workspaceId, cancellationToken);
        if (!await dbContext.Opportunities.AnyAsync(x => x.Id == opportunityId && x.WorkspaceId == workspaceId, cancellationToken))
            return Missing();
        var invalid = ValidateInput(request, request.ExternalId, request.SourceUrl);
        if (invalid is not null) return invalid;
        var firstSeenAt = request.FirstSeenAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var lastSeenAt = request.LastSeenAt?.ToUniversalTime();
        if (lastSeenAt < firstSeenAt) return Invalid(OpportunitySourceErrorCode.InvalidTimestamps, "LastSeenAt must not precede FirstSeenAt.");
        var (configurationId, searchId, error) = await ResolveReferencesAsync(workspaceId,
            request.SourceConfigurationId, request.SavedSearchId, request.SourceExecutionId, cancellationToken);
        if (error is not null) return Invalid(OpportunitySourceErrorCode.InvalidReference, error);
        if (request.ExternalId is not null && configurationId is null)
            return Invalid(OpportunitySourceErrorCode.InvalidReference, "ExternalId requires a source configuration.");
        var normalized = IngestionNormalization.UrlKey(request.SourceUrl);
        var duplicate = await ValidateDuplicatesAsync(workspaceId, null, configurationId, request.ExternalId, normalized, cancellationToken);
        if (duplicate is not null) return duplicate;
        var source = new OpportunitySource
        {
            WorkspaceId = workspaceId, OpportunityId = opportunityId, SourceConfigurationId = configurationId,
            SavedSearchId = searchId, SourceExecutionId = request.SourceExecutionId,
            SourceLabel = request.SourceLabel, SourceUrl = request.SourceUrl, NormalizedSourceUrl = normalized,
            ExternalId = request.ExternalId, FirstSeenAt = firstSeenAt, LastSeenAt = lastSeenAt
        };
        dbContext.OpportunitySources.Add(source);
        var failure = await SaveAsync(cancellationToken);
        if (failure is not null) return failure;
        await dbContext.Entry(source).ReloadAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(OpportunitySourceWriteStatus.Succeeded, ToDto(source));
    }

    public async Task<OpportunitySourceWriteResult> UpdateAsync(Guid opportunityId, Guid id,
        UpdateOpportunitySourceRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await WorkspaceIdentityLock.AcquireAsync(dbContext, workspaceId, cancellationToken);
        var source = await dbContext.OpportunitySources.SingleOrDefaultAsync(x => x.Id == id
            && x.OpportunityId == opportunityId && x.WorkspaceId == workspaceId, cancellationToken);
        if (source is null) return Missing();
        await dbContext.Entry(source).ReloadAsync(cancellationToken);
        if (dbContext.Entry(source).State == EntityState.Detached) return Missing();
        var identical = source.SourceConfigurationId == request.SourceConfigurationId && source.SavedSearchId == request.SavedSearchId
            && source.SourceExecutionId == request.SourceExecutionId && source.SourceLabel == request.SourceLabel
            && source.SourceUrl == request.SourceUrl && source.ExternalId == request.ExternalId
            && source.LastSeenAt == request.LastSeenAt?.ToUniversalTime();
        if (identical)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(OpportunitySourceWriteStatus.Succeeded);
        }
        if (await IsUsedAsync(source, cancellationToken))
            return Conflict(OpportunitySourceErrorCode.ImmutableSourceIdentity, "An observed source identity cannot be modified.");
        var invalid = ValidateInput(request, request.ExternalId, request.SourceUrl);
        if (invalid is not null) return invalid;
        var lastSeenAt = request.LastSeenAt?.ToUniversalTime();
        if (lastSeenAt < source.FirstSeenAt) return Invalid(OpportunitySourceErrorCode.InvalidTimestamps, "LastSeenAt must not precede FirstSeenAt.");
        var (configurationId, searchId, error) = await ResolveReferencesAsync(workspaceId,
            request.SourceConfigurationId, request.SavedSearchId, request.SourceExecutionId, cancellationToken);
        if (error is not null) return Invalid(OpportunitySourceErrorCode.InvalidReference, error);
        if (request.ExternalId is not null && configurationId is null)
            return Invalid(OpportunitySourceErrorCode.InvalidReference, "ExternalId requires a source configuration.");
        var normalized = IngestionNormalization.UrlKey(request.SourceUrl);
        var duplicate = await ValidateDuplicatesAsync(workspaceId, id, configurationId, request.ExternalId, normalized, cancellationToken);
        if (duplicate is not null) return duplicate;
        source.SourceConfigurationId = configurationId;
        source.SavedSearchId = searchId;
        source.SourceExecutionId = request.SourceExecutionId;
        source.SourceLabel = request.SourceLabel;
        source.SourceUrl = request.SourceUrl;
        source.NormalizedSourceUrl = normalized;
        source.ExternalId = request.ExternalId;
        source.LastSeenAt = lastSeenAt;
        var failure = await SaveAsync(cancellationToken);
        if (failure is not null) return failure;
        await transaction.CommitAsync(cancellationToken);
        return new(OpportunitySourceWriteStatus.Succeeded);
    }

    public async Task<OpportunitySourceWriteResult> DeleteAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await WorkspaceIdentityLock.AcquireAsync(dbContext, workspaceId, cancellationToken);
        var source = await dbContext.OpportunitySources.SingleOrDefaultAsync(x => x.Id == id
            && x.OpportunityId == opportunityId && x.WorkspaceId == workspaceId, cancellationToken);
        if (source is null) return Missing();
        await dbContext.Entry(source).ReloadAsync(cancellationToken);
        if (dbContext.Entry(source).State == EntityState.Detached) return Missing();
        if (await IsUsedAsync(source, cancellationToken))
            return Conflict(OpportunitySourceErrorCode.SourceIdentityInUse, "An observed source identity cannot be deleted.");
        dbContext.OpportunitySources.Remove(source);
        var failure = await SaveAsync(cancellationToken);
        if (failure is not null) return failure;
        await transaction.CommitAsync(cancellationToken);
        return new(OpportunitySourceWriteStatus.Succeeded);
    }

    private Task<bool> IsUsedAsync(OpportunitySource source, CancellationToken cancellationToken) =>
        source.SourceExecutionId.HasValue ? Task.FromResult(true)
            : dbContext.SourceExecutionItemSources.AnyAsync(x => x.OpportunitySourceId == source.Id, cancellationToken);

    private static OpportunitySourceWriteResult? ValidateInput(object request, string? externalId, string? url)
    {
        if (!Validator.TryValidateObject(request, new ValidationContext(request), null, true))
            return Invalid(OpportunitySourceErrorCode.InvalidInput, "Source fields must respect their required values and lengths.");
        if (url is not null && IngestionNormalization.UrlKey(url) is null)
            return Invalid(OpportunitySourceErrorCode.InvalidSourceUrl, "SourceUrl must be an absolute HTTP(S) URL without credentials.");
        if ((externalId is null && url is null) || (externalId is not null && string.IsNullOrWhiteSpace(externalId)))
            return Invalid(OpportunitySourceErrorCode.MissingSourceIdentity, "A source needs a nonblank ExternalId or SourceUrl.");
        return null;
    }

    private async Task<OpportunitySourceWriteResult?> ValidateDuplicatesAsync(Guid workspaceId, Guid? currentId,
        Guid? configurationId, string? externalId, string? normalizedUrl, CancellationToken cancellationToken)
    {
        var sources = dbContext.OpportunitySources.AsNoTracking().Where(x => x.WorkspaceId == workspaceId
            && (!currentId.HasValue || x.Id != currentId));
        if (externalId is not null && await sources.AnyAsync(x => x.SourceConfigurationId == configurationId
                && x.ExternalId == externalId, cancellationToken)) return DuplicateExternal();
        if (normalizedUrl is not null && await sources.AnyAsync(x => x.NormalizedSourceUrl == normalizedUrl, cancellationToken))
            return DuplicateUrl();
        return null;
    }

    private async Task<OpportunitySourceWriteResult?> SaveAsync(CancellationToken cancellationToken)
    {
        try { await dbContext.SaveChangesAsync(cancellationToken); return null; }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "UX_OpportunitySources_Workspace_SourceConfiguration_ExternalId" or "UX_OpportunitySources_Workspace_NormalizedSourceUrl"
        })
        {
            dbContext.ChangeTracker.Clear();
            return ((PostgresException)exception.InnerException).ConstraintName == "UX_OpportunitySources_Workspace_SourceConfiguration_ExternalId"
                ? DuplicateExternal() : DuplicateUrl();
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return Conflict(OpportunitySourceErrorCode.ConcurrentIdentityChange, "The source identity was concurrently changed.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: not null } postgres
            && (postgres.ConstraintName.StartsWith("FK_OpportunitySources_", StringComparison.Ordinal)
                || postgres.ConstraintName.StartsWith("FK_SourceExecutionItemSources_OpportunitySources_", StringComparison.Ordinal)))
        {
            dbContext.ChangeTracker.Clear();
            return Conflict(OpportunitySourceErrorCode.ConcurrentIdentityChange, "A source reference was concurrently changed.");
        }
    }

    private static OpportunitySourceWriteResult Missing() => new(OpportunitySourceWriteStatus.NotFound,
        Code: OpportunitySourceErrorCode.InvalidReference, Detail: "The opportunity or source does not exist in the current workspace.");
    private static OpportunitySourceWriteResult Invalid(OpportunitySourceErrorCode code, string detail) =>
        new(OpportunitySourceWriteStatus.InvalidInput, Code: code, Detail: detail);
    private static OpportunitySourceWriteResult Conflict(OpportunitySourceErrorCode code, string detail) =>
        new(OpportunitySourceWriteStatus.Conflict, Code: code, Detail: detail);
    private static OpportunitySourceWriteResult DuplicateExternal() => Conflict(OpportunitySourceErrorCode.DuplicateExternalId,
        "This external identifier is already linked in the source configuration.");
    private static OpportunitySourceWriteResult DuplicateUrl() => Conflict(OpportunitySourceErrorCode.DuplicateSourceUrl,
        "This normalized URL is already linked in the workspace.");

    private async Task<(Guid? SourceConfigurationId, Guid? SavedSearchId, string? Error)> ResolveReferencesAsync(
        Guid workspaceId, Guid? sourceConfigurationId, Guid? savedSearchId, Guid? sourceExecutionId,
        CancellationToken cancellationToken)
    {
        // Resolve the execution first, then validate every explicit or inferred reference.
        // Archived references are legitimate provenance and remain usable here.
        if (sourceExecutionId.HasValue)
        {
            var execution = await dbContext.SourceExecutions.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == sourceExecutionId.Value && x.WorkspaceId == workspaceId, cancellationToken);
            if (execution is null)
                return (null, null, "SourceExecutionId must reference an execution in the current workspace.");
            if (sourceConfigurationId.HasValue && sourceConfigurationId.Value != execution.SourceConfigurationId)
                return (null, null, "SourceConfigurationId does not match the source execution.");
            sourceConfigurationId ??= execution.SourceConfigurationId;
            if (execution.SavedSearchId.HasValue)
            {
                if (savedSearchId.HasValue && savedSearchId.Value != execution.SavedSearchId.Value)
                    return (null, null, "SavedSearchId does not match the source execution.");
                savedSearchId ??= execution.SavedSearchId;
            }
        }
        if (savedSearchId.HasValue)
        {
            var search = await dbContext.SavedSearches.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == savedSearchId.Value && x.WorkspaceId == workspaceId, cancellationToken);
            if (search is null)
                return (null, null, "SavedSearchId must reference a saved search in the current workspace.");
            if (sourceConfigurationId.HasValue && sourceConfigurationId.Value != search.SourceConfigurationId)
                return (null, null, "SourceConfigurationId does not match the saved search.");
            sourceConfigurationId ??= search.SourceConfigurationId;
        }
        if (sourceConfigurationId.HasValue && !await dbContext.SourceConfigurations.AnyAsync(
                x => x.Id == sourceConfigurationId.Value && x.WorkspaceId == workspaceId, cancellationToken))
            return (null, null, "SourceConfigurationId must reference a configuration in the current workspace.");
        return (sourceConfigurationId, savedSearchId, null);
    }

    private static OpportunitySourceDto ToDto(OpportunitySource source) => new()
    {
        Id = source.Id, WorkspaceId = source.WorkspaceId, OpportunityId = source.OpportunityId,
        SourceConfigurationId = source.SourceConfigurationId, SavedSearchId = source.SavedSearchId,
        SourceExecutionId = source.SourceExecutionId, SourceLabel = source.SourceLabel,
        SourceUrl = source.SourceUrl, NormalizedSourceUrl = source.NormalizedSourceUrl, ExternalId = source.ExternalId,
        FirstSeenAt = source.FirstSeenAt, LastSeenAt = source.LastSeenAt
    };
}
