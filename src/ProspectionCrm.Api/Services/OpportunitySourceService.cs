using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.OpportunitySources;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class OpportunitySourceService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : IOpportunitySourceService
{
    private const string ExternalIdConflict = "This external source identifier is already linked to another opportunity source.";
    private const string SourceUrlConflict = "This source URL is already linked to this opportunity.";

    public async Task<IReadOnlyList<OpportunitySourceDto>?> GetAllAsync(Guid opportunityId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.Opportunities.AnyAsync(x => x.Id == opportunityId && x.WorkspaceId == workspaceId, cancellationToken))
            return null;
        var sources = await dbContext.OpportunitySources.AsNoTracking()
            .Where(x => x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspaceId)
            .OrderBy(x => x.FirstSeenAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return sources.Select(ToDto).ToList();
    }

    public async Task<OpportunitySourceDto?> GetByIdAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var source = await dbContext.OpportunitySources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id
            && x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspaceId, cancellationToken);
        return source is null ? null : ToDto(source);
    }

    public async Task<(bool Found, OpportunitySourceDto? Source, string? Error)> CreateAsync(Guid opportunityId,
        CreateOpportunitySourceRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.Opportunities.AnyAsync(x => x.Id == opportunityId && x.WorkspaceId == workspaceId, cancellationToken))
            return (false, null, null);
        var firstSeenAt = request.FirstSeenAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var lastSeenAt = request.LastSeenAt?.ToUniversalTime();
        if (lastSeenAt.HasValue && lastSeenAt.Value < firstSeenAt)
            return (true, null, "LastSeenAt must be greater than or equal to FirstSeenAt.");
        var (sourceConfigurationId, savedSearchId, error) = await ResolveReferencesAsync(workspaceId,
            request.SourceConfigurationId, request.SavedSearchId, request.SourceExecutionId, cancellationToken);
        if (error is not null)
            return (true, null, error);
        error = await ValidateDuplicatesAsync(workspaceId, opportunityId, null, sourceConfigurationId,
            request.ExternalId, request.SourceUrl, cancellationToken);
        if (error is not null)
            return (true, null, error);
        var source = new OpportunitySource
        {
            OpportunityId = opportunityId,
            SourceConfigurationId = sourceConfigurationId,
            SavedSearchId = savedSearchId,
            SourceExecutionId = request.SourceExecutionId,
            SourceLabel = request.SourceLabel,
            SourceUrl = request.SourceUrl,
            ExternalId = request.ExternalId,
            FirstSeenAt = firstSeenAt,
            LastSeenAt = lastSeenAt
        };
        dbContext.OpportunitySources.Add(source);
        error = await SaveSourceAsync(source, cancellationToken);
        if (error is not null)
            return (true, null, error);
        return (true, ToDto(source), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid opportunityId, Guid id,
        UpdateOpportunitySourceRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var source = await dbContext.OpportunitySources.SingleOrDefaultAsync(x => x.Id == id
            && x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspaceId, cancellationToken);
        if (source is null)
            return (false, null);
        var lastSeenAt = request.LastSeenAt?.ToUniversalTime();
        if (lastSeenAt.HasValue && lastSeenAt.Value < source.FirstSeenAt)
            return (true, "LastSeenAt must be greater than or equal to FirstSeenAt.");
        var (sourceConfigurationId, savedSearchId, error) = await ResolveReferencesAsync(workspaceId,
            request.SourceConfigurationId, request.SavedSearchId, request.SourceExecutionId, cancellationToken);
        if (error is not null)
            return (true, error);
        error = await ValidateDuplicatesAsync(workspaceId, opportunityId, id, sourceConfigurationId,
            request.ExternalId, request.SourceUrl, cancellationToken);
        if (error is not null)
            return (true, error);
        source.SourceConfigurationId = sourceConfigurationId;
        source.SavedSearchId = savedSearchId;
        source.SourceExecutionId = request.SourceExecutionId;
        source.SourceLabel = request.SourceLabel;
        source.SourceUrl = request.SourceUrl;
        source.ExternalId = request.ExternalId;
        source.LastSeenAt = lastSeenAt;
        error = await SaveSourceAsync(source, cancellationToken);
        return (true, error);
    }

    public async Task<bool> DeleteAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var source = await dbContext.OpportunitySources.SingleOrDefaultAsync(x => x.Id == id
            && x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspaceId, cancellationToken);
        if (source is null)
            return false;
        dbContext.OpportunitySources.Remove(source);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

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

    private async Task<string?> ValidateDuplicatesAsync(Guid workspaceId, Guid opportunityId, Guid? currentId,
        Guid? sourceConfigurationId, string? externalId, string? sourceUrl, CancellationToken cancellationToken)
    {
        var otherSources = dbContext.OpportunitySources.AsNoTracking()
            .Where(x => x.Opportunity.WorkspaceId == workspaceId && (!currentId.HasValue || x.Id != currentId.Value));
        if (sourceConfigurationId.HasValue && externalId is not null && await otherSources.AnyAsync(
                x => x.SourceConfigurationId == sourceConfigurationId.Value && x.ExternalId == externalId, cancellationToken))
            return ExternalIdConflict;
        if (sourceUrl is not null && await otherSources.AnyAsync(
                x => x.OpportunityId == opportunityId && x.SourceUrl == sourceUrl, cancellationToken))
            return SourceUrlConflict;
        return null;
    }

    private async Task<string?> SaveSourceAsync(OpportunitySource source, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "UX_OpportunitySources_SourceConfiguration_ExternalId" or "UX_OpportunitySources_Opportunity_SourceUrl"
        })
        {
            // A concurrent request may insert a duplicate after the explicit precheck.
            dbContext.Entry(source).State = EntityState.Detached;
            var postgresException = (PostgresException)exception.InnerException;
            return postgresException.ConstraintName == "UX_OpportunitySources_SourceConfiguration_ExternalId"
                ? ExternalIdConflict : SourceUrlConflict;
        }
    }

    private static OpportunitySourceDto ToDto(OpportunitySource source) => new()
    {
        Id = source.Id,
        OpportunityId = source.OpportunityId,
        SourceConfigurationId = source.SourceConfigurationId,
        SavedSearchId = source.SavedSearchId,
        SourceExecutionId = source.SourceExecutionId,
        SourceLabel = source.SourceLabel,
        SourceUrl = source.SourceUrl,
        ExternalId = source.ExternalId,
        FirstSeenAt = source.FirstSeenAt,
        LastSeenAt = source.LastSeenAt
    };
}
