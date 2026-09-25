using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Campaigns;
using ProspectionCrm.Api.Dtos.CampaignOpportunities;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class CampaignService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : ICampaignService
{
    public async Task<IReadOnlyList<CampaignDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entities = await dbContext.Campaigns.AsNoTracking().Include(x => x.CampaignOpportunities.Where(m => m.Opportunity.WorkspaceId == workspaceId))
            .Where(x => x.WorkspaceId == workspaceId && (includeArchived || x.ArchivedAt == null))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<CampaignDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Campaigns.AsNoTracking().Include(x => x.CampaignOpportunities.Where(m => m.Opportunity.WorkspaceId == workspaceId))
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(CampaignDto? Campaign, string? Error)> CreateAsync(CreateCampaignRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var error = await ValidateAsync(workspaceId, request.PipelineId, request.StatusCode, request.StartsAt, request.EndsAt, true, cancellationToken);
        if (error is not null)
            return (null, error);
        var entity = new Campaign
        {
            WorkspaceId = workspaceId,
            PipelineId = request.PipelineId,
            Name = request.Name,
            Description = request.Description,
            StatusCode = request.StatusCode,
            StartsAt = request.StartsAt?.ToUniversalTime(),
            EndsAt = request.EndsAt?.ToUniversalTime()
        };
        dbContext.Campaigns.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(entity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid id, UpdateCampaignRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Campaigns
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        var error = await ValidateAsync(workspaceId, request.PipelineId, request.StatusCode, request.StartsAt, request.EndsAt, request.PipelineId != entity.PipelineId, cancellationToken);
        if (error is not null)
            return (true, error);
        entity.PipelineId = request.PipelineId;
        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.StatusCode = request.StatusCode;
        entity.StartsAt = request.StartsAt?.ToUniversalTime();
        entity.EndsAt = request.EndsAt?.ToUniversalTime();
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Campaigns
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return false;
        var now = DateTimeOffset.UtcNow;
        entity.ArchivedAt = now;
        entity.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<(bool Found, string? Error)> RestoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Campaigns
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        var error = await ValidateAsync(workspaceId, entity.PipelineId, entity.StatusCode, entity.StartsAt, entity.EndsAt, true, cancellationToken);
        if (error is not null)
            return (true, error);
        entity.ArchivedAt = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private async Task<string?> ValidateAsync(Guid workspaceId, Guid? pipelineId, string statusCode, DateTimeOffset? startsAt, DateTimeOffset? endsAt, bool requireActivePipeline, CancellationToken cancellationToken)
    {
        if (statusCode is not ("draft" or "active" or "paused" or "completed" or "cancelled"))
            return "StatusCode must be draft, active, paused, completed or cancelled.";
        if (startsAt.HasValue && endsAt.HasValue && endsAt.Value < startsAt.Value)
            return "EndsAt must be greater than or equal to StartsAt.";
        if (pipelineId.HasValue && !await dbContext.Pipelines.AnyAsync(x => x.Id == pipelineId.Value
                && x.WorkspaceId == workspaceId && (!requireActivePipeline || x.ArchivedAt == null), cancellationToken))
            return "PipelineId must reference a pipeline in the current workspace, active for a new selection or restore.";
        return null;
    }

    public async Task<(bool Found, string? Error)> AddOpportunityAsync(Guid campaignId, Guid opportunityId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var campaign = await dbContext.Campaigns.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == campaignId && x.WorkspaceId == workspaceId, cancellationToken);
        if (campaign is null)
            return (false, null);
        if (campaign.ArchivedAt.HasValue)
            return (true, "An archived campaign cannot receive new opportunities.");
        if (!await dbContext.Opportunities.AnyAsync(x => x.Id == opportunityId && x.WorkspaceId == workspaceId, cancellationToken))
            return (false, null);
        if (await dbContext.CampaignOpportunities.AnyAsync(x => x.CampaignId == campaignId && x.OpportunityId == opportunityId
                && x.Campaign.WorkspaceId == workspaceId && x.Opportunity.WorkspaceId == workspaceId, cancellationToken))
            return (true, null);
        var membership = new CampaignOpportunity { CampaignId = campaignId, OpportunityId = opportunityId };
        dbContext.CampaignOpportunities.Add(membership);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "UX_CampaignOpportunities_Campaign_Opportunity"
        })
        {
            // Concurrent identical additions have the same successful, idempotent outcome.
            dbContext.Entry(membership).State = EntityState.Detached;
        }
        return (true, null);
    }

    public async Task<bool> RemoveOpportunityAsync(Guid campaignId, Guid opportunityId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.Campaigns.AnyAsync(x => x.Id == campaignId && x.WorkspaceId == workspaceId, cancellationToken)
            || !await dbContext.Opportunities.AnyAsync(x => x.Id == opportunityId && x.WorkspaceId == workspaceId, cancellationToken))
            return false;
        var membership = await dbContext.CampaignOpportunities.SingleOrDefaultAsync(x => x.CampaignId == campaignId
            && x.OpportunityId == opportunityId && x.Campaign.WorkspaceId == workspaceId
            && x.Opportunity.WorkspaceId == workspaceId, cancellationToken);
        if (membership is not null)
        {
            dbContext.CampaignOpportunities.Remove(membership);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    private static CampaignDto ToDto(Campaign entity) => new()
    {
        Id = entity.Id,
        PipelineId = entity.PipelineId,
        Name = entity.Name,
        Description = entity.Description,
        StatusCode = entity.StatusCode,
        StartsAt = entity.StartsAt,
        EndsAt = entity.EndsAt,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        ArchivedAt = entity.ArchivedAt,
        Opportunities = entity.CampaignOpportunities.OrderBy(x => x.AddedAt).ThenBy(x => x.Id)
            .Select(x => new CampaignOpportunityDto { Id = x.Id, CampaignId = x.CampaignId, OpportunityId = x.OpportunityId, AddedAt = x.AddedAt }).ToList()
    };
}
