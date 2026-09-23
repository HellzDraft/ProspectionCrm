using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Applications;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class ApplicationService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : IApplicationService
{
    public async Task<IReadOnlyList<ApplicationDto>?> GetAllAsync(Guid opportunityId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.Opportunities.AnyAsync(x => x.Id == opportunityId && x.WorkspaceId == workspaceId, cancellationToken))
            return null;
        var entities = await dbContext.Applications.AsNoTracking()
            .Where(x => x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<ApplicationDto?> GetByIdAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Applications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id
            && x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(bool Found, ApplicationDto? Application, string? Error)> CreateAsync(Guid opportunityId,
        CreateApplicationRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var opportunity = await dbContext.Opportunities.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == opportunityId && x.WorkspaceId == workspaceId, cancellationToken);
        if (opportunity is null)
            return (false, null, null);
        if (opportunity.ArchivedAt.HasValue)
            return (true, null, "Cannot create an application for an archived opportunity.");
        var error = Validate(request.StatusCode);
        if (error is not null)
            return (true, null, error);
        var entity = new Application
        {
            OpportunityId = opportunityId,
            StatusCode = request.StatusCode,
            SubmittedAt = request.SubmittedAt?.ToUniversalTime(),
            ChannelCode = request.ChannelCode,
            Notes = request.Notes
        };
        dbContext.Applications.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, ToDto(entity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid opportunityId, Guid id,
        UpdateApplicationRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Applications.SingleOrDefaultAsync(x => x.Id == id
            && x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        var error = Validate(request.StatusCode);
        if (error is not null)
            return (true, error);
        entity.StatusCode = request.StatusCode;
        entity.SubmittedAt = request.SubmittedAt?.ToUniversalTime();
        entity.ChannelCode = request.ChannelCode;
        entity.Notes = request.Notes;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> DeleteAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Applications.SingleOrDefaultAsync(x => x.Id == id
            && x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return false;
        dbContext.Applications.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string? Validate(string statusCode)
    {
        if (statusCode is not ("draft" or "prepared" or "submitted" or "acknowledged" or "accepted" or "rejected" or "withdrawn"))
            return "StatusCode must be draft, prepared, submitted, acknowledged, accepted, rejected or withdrawn.";
        return null;
    }

    private static ApplicationDto ToDto(Application entity) => new()
    {
        Id = entity.Id,
        OpportunityId = entity.OpportunityId,
        StatusCode = entity.StatusCode,
        SubmittedAt = entity.SubmittedAt,
        ChannelCode = entity.ChannelCode,
        Notes = entity.Notes,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };
}
