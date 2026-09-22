using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Opportunities;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class OpportunityService(ProspectionCrmDbContext dbContext) : IOpportunityService
{
    public async Task<IReadOnlyList<OpportunityDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var opportunities = await dbContext.Opportunities.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return opportunities.Select(ToDto).ToList();
    }

    public async Task<OpportunityDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var opportunity = await dbContext.Opportunities.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return opportunity is null ? null : ToDto(opportunity);
    }

    public async Task<(OpportunityDto? Opportunity, string? Error)> CreateAsync(
        CreateOpportunityRequest request, CancellationToken cancellationToken)
    {
        var error = await ValidateReferencesAsync(request.CompanyId, request.ContactId, cancellationToken);
        if (error is not null)
            return (null, error);

        var opportunity = new Opportunity
        {
            Id = Guid.NewGuid(),
            Title = request.Title,
            CompanyId = request.CompanyId,
            ContactId = request.ContactId,
            PipelineCode = request.PipelineCode,
            StatusCode = request.StatusCode,
            PriorityCode = request.PriorityCode,
            Location = request.Location,
            SourceName = request.SourceName,
            SourceUrl = request.SourceUrl,
            Notes = request.Notes,
            FollowUpDueAt = request.FollowUpDueAt?.ToUniversalTime(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = null
        };

        dbContext.Opportunities.Add(opportunity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (ToDto(opportunity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(
        Guid id, UpdateOpportunityRequest request, CancellationToken cancellationToken)
    {
        var opportunity = await dbContext.Opportunities
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (opportunity is null)
            return (false, null);

        var error = await ValidateReferencesAsync(request.CompanyId, request.ContactId, cancellationToken);
        if (error is not null)
            return (true, error);

        opportunity.Title = request.Title;
        opportunity.CompanyId = request.CompanyId;
        opportunity.ContactId = request.ContactId;
        opportunity.PipelineCode = request.PipelineCode;
        opportunity.StatusCode = request.StatusCode;
        opportunity.PriorityCode = request.PriorityCode;
        opportunity.Location = request.Location;
        opportunity.SourceName = request.SourceName;
        opportunity.SourceUrl = request.SourceUrl;
        opportunity.Notes = request.Notes;
        opportunity.FollowUpDueAt = request.FollowUpDueAt?.ToUniversalTime();
        opportunity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var opportunity = await dbContext.Opportunities
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (opportunity is null)
            return false;

        dbContext.Opportunities.Remove(opportunity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<string?> ValidateReferencesAsync(
        Guid? companyId, Guid? contactId, CancellationToken cancellationToken)
    {
        if (companyId.HasValue &&
            !await dbContext.Companies.AnyAsync(x => x.Id == companyId.Value, cancellationToken))
            return "CompanyId does not reference an existing company.";

        if (contactId.HasValue &&
            !await dbContext.Contacts.AnyAsync(x => x.Id == contactId.Value, cancellationToken))
            return "ContactId does not reference an existing contact.";

        return null;
    }

    private static OpportunityDto ToDto(Opportunity opportunity) => new()
    {
        Id = opportunity.Id,
        Title = opportunity.Title,
        CompanyId = opportunity.CompanyId,
        ContactId = opportunity.ContactId,
        PipelineCode = opportunity.PipelineCode,
        StatusCode = opportunity.StatusCode,
        PriorityCode = opportunity.PriorityCode,
        Location = opportunity.Location,
        SourceName = opportunity.SourceName,
        SourceUrl = opportunity.SourceUrl,
        Notes = opportunity.Notes,
        FollowUpDueAt = opportunity.FollowUpDueAt,
        CreatedAt = opportunity.CreatedAt,
        UpdatedAt = opportunity.UpdatedAt,
    };
}
