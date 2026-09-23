using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Opportunities;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class OpportunityService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : IOpportunityService
{
    public async Task<IReadOnlyList<OpportunityDto>> GetAllAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var opportunities = await dbContext.Opportunities.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && (includeArchived || x.ArchivedAt == null))
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return opportunities.Select(ToDto).ToList();
    }

    public async Task<OpportunityDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var opportunity = await dbContext.Opportunities.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return opportunity is null ? null : ToDto(opportunity);
    }

    public async Task<(OpportunityDto? Opportunity, string? Error)> CreateAsync(
        CreateOpportunityRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var error = await ValidateReferencesAsync(workspaceId, request.CompanyId, request.ContactId,
            request.PipelineStageId, requireActiveStage: true, request.PriorityCode, cancellationToken);
        if (error is not null)
            return (null, error);

        var opportunity = new Opportunity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Title = request.Title,
            CompanyId = request.CompanyId,
            ContactId = request.ContactId,
            PipelineStageId = request.PipelineStageId!.Value,
            PriorityCode = request.PriorityCode,
            Location = request.Location,
            Notes = request.Notes,
            CreatedAt = DateTimeOffset.UtcNow
        };
        dbContext.Opportunities.Add(opportunity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(opportunity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(
        Guid id, UpdateOpportunityRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var opportunity = await dbContext.Opportunities
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (opportunity is null)
            return (false, null);

        var error = await ValidateReferencesAsync(workspaceId, request.CompanyId, request.ContactId,
            request.PipelineStageId, requireActiveStage: request.PipelineStageId != opportunity.PipelineStageId,
            request.PriorityCode, cancellationToken);
        if (error is not null)
            return (true, error);

        opportunity.Title = request.Title;
        opportunity.CompanyId = request.CompanyId;
        opportunity.ContactId = request.ContactId;
        opportunity.PipelineStageId = request.PipelineStageId!.Value;
        opportunity.PriorityCode = request.PriorityCode;
        opportunity.Location = request.Location;
        opportunity.Notes = request.Notes;
        opportunity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken)
        => SetArchivedAsync(id, archive: true, cancellationToken);

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
        => SetArchivedAsync(id, archive: false, cancellationToken);

    private async Task<bool> SetArchivedAsync(Guid id, bool archive, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var opportunity = await dbContext.Opportunities
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (opportunity is null)
            return false;
        opportunity.ArchivedAt = archive ? DateTimeOffset.UtcNow : null;
        opportunity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Exceptional hard deletion; the normal UI archives opportunities.
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var opportunity = await dbContext.Opportunities
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (opportunity is null)
            return false;
        dbContext.Opportunities.Remove(opportunity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<string?> ValidateReferencesAsync(Guid workspaceId, Guid? companyId, Guid? contactId,
        Guid? pipelineStageId, bool requireActiveStage, string priorityCode, CancellationToken cancellationToken)
    {
        if (priorityCode is not ("low" or "normal" or "high"))
            return "PriorityCode must be low, normal or high.";
        if (companyId.HasValue && !await dbContext.Companies.AnyAsync(
                x => x.Id == companyId.Value && x.WorkspaceId == workspaceId, cancellationToken))
            return "CompanyId does not reference a company in the current workspace.";
        if (contactId.HasValue && !await dbContext.Contacts.AnyAsync(
                x => x.Id == contactId.Value && x.WorkspaceId == workspaceId, cancellationToken))
            return "ContactId does not reference a contact in the current workspace.";
        if (!pipelineStageId.HasValue || !await dbContext.PipelineStages.AnyAsync(
                x => x.Id == pipelineStageId.Value && x.Pipeline.WorkspaceId == workspaceId
                    && (!requireActiveStage || (x.ArchivedAt == null && x.Pipeline.ArchivedAt == null)), cancellationToken))
            return "PipelineStageId must reference a stage in the current workspace, with an active stage and pipeline for a new selection.";
        return null;
    }

    private static OpportunityDto ToDto(Opportunity opportunity) => new()
    {
        Id = opportunity.Id,
        Title = opportunity.Title,
        CompanyId = opportunity.CompanyId,
        ContactId = opportunity.ContactId,
        PipelineStageId = opportunity.PipelineStageId,
        PriorityCode = opportunity.PriorityCode,
        Score = opportunity.Score,
        ScoredAt = opportunity.ScoredAt,
        Location = opportunity.Location,
        Notes = opportunity.Notes,
        CreatedAt = opportunity.CreatedAt,
        UpdatedAt = opportunity.UpdatedAt,
        ArchivedAt = opportunity.ArchivedAt
    };
}
