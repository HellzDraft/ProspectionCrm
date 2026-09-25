using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Proposals;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class ProposalService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : IProposalService
{
    public async Task<IReadOnlyList<ProposalDto>?> GetAllAsync(Guid opportunityId, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await dbContext.Opportunities.AnyAsync(x => x.Id == opportunityId && x.WorkspaceId == workspaceId, cancellationToken))
            return null;
        var entities = await dbContext.Proposals.AsNoTracking()
            .Where(x => x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<ProposalDto?> GetByIdAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Proposals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id
            && x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspaceId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<(bool Found, ProposalDto? Proposal, string? Error)> CreateAsync(Guid opportunityId,
        CreateProposalRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var opportunity = await dbContext.Opportunities.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == opportunityId && x.WorkspaceId == workspaceId, cancellationToken);
        if (opportunity is null)
            return (false, null, null);
        if (opportunity.ArchivedAt.HasValue)
            return (true, null, "Cannot create a proposal for an archived opportunity.");
        var error = Validate(request.StatusCode, request.Amount, request.CurrencyCode,
            request.RateTypeCode, request.SentAt, request.ValidUntil);
        if (error is not null)
            return (true, null, error);
        error = await ProfessionalReferenceValidation.DocumentAsync(dbContext, workspaceId, request.DocumentId,
            "proposal", true, nameof(request.DocumentId), cancellationToken);
        if (error is not null)
            return (true, null, error);
        var entity = new Proposal
        {
            OpportunityId = opportunityId,
            DocumentId = request.DocumentId,
            StatusCode = request.StatusCode,
            Amount = request.Amount,
            CurrencyCode = request.CurrencyCode,
            RateTypeCode = request.RateTypeCode,
            SentAt = request.SentAt?.ToUniversalTime(),
            ValidUntil = request.ValidUntil?.ToUniversalTime(),
            Notes = request.Notes
        };
        dbContext.Proposals.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, ToDto(entity), null);
    }

    public async Task<(bool Found, string? Error)> UpdateAsync(Guid opportunityId, Guid id,
        UpdateProposalRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Proposals.SingleOrDefaultAsync(x => x.Id == id
            && x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return (false, null);
        var error = Validate(request.StatusCode, request.Amount, request.CurrencyCode,
            request.RateTypeCode, request.SentAt, request.ValidUntil);
        if (error is not null)
            return (true, error);
        error = await ProfessionalReferenceValidation.DocumentAsync(dbContext, workspaceId, request.DocumentId,
            "proposal", request.DocumentId != entity.DocumentId, nameof(request.DocumentId), cancellationToken);
        if (error is not null)
            return (true, error);
        entity.DocumentId = request.DocumentId;
        entity.StatusCode = request.StatusCode;
        entity.Amount = request.Amount;
        entity.CurrencyCode = request.CurrencyCode;
        entity.RateTypeCode = request.RateTypeCode;
        entity.SentAt = request.SentAt?.ToUniversalTime();
        entity.ValidUntil = request.ValidUntil?.ToUniversalTime();
        entity.Notes = request.Notes;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> DeleteAsync(Guid opportunityId, Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var entity = await dbContext.Proposals.SingleOrDefaultAsync(x => x.Id == id
            && x.OpportunityId == opportunityId && x.Opportunity.WorkspaceId == workspaceId, cancellationToken);
        if (entity is null)
            return false;
        dbContext.Proposals.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string? Validate(string statusCode, decimal? amount, string? currencyCode,
        string? rateTypeCode, DateTimeOffset? sentAt, DateTimeOffset? validUntil)
    {
        if (statusCode is not ("draft" or "sent" or "negotiating" or "accepted" or "rejected" or "expired" or "withdrawn"))
            return "StatusCode must be draft, sent, negotiating, accepted, rejected, expired or withdrawn.";
        if (amount.HasValue && amount.Value < 0)
            return "Amount must be greater than or equal to zero.";
        if (amount.HasValue && currencyCode is null)
            return "CurrencyCode is required when Amount is provided.";
        if (currencyCode is not null && (currencyCode.Length != 3 || currencyCode.Any(c => c is < 'A' or > 'Z')))
            return "CurrencyCode must contain exactly three uppercase letters from A to Z.";
        if (rateTypeCode is not (null or "fixed" or "hourly" or "daily" or "monthly" or "other"))
            return "RateTypeCode must be fixed, hourly, daily, monthly or other when provided.";
        if (sentAt.HasValue && validUntil.HasValue && validUntil.Value < sentAt.Value)
            return "ValidUntil must be greater than or equal to SentAt.";
        return null;
    }

    private static ProposalDto ToDto(Proposal entity) => new()
    {
        Id = entity.Id,
        OpportunityId = entity.OpportunityId,
        DocumentId = entity.DocumentId,
        StatusCode = entity.StatusCode,
        Amount = entity.Amount,
        CurrencyCode = entity.CurrencyCode,
        RateTypeCode = entity.RateTypeCode,
        SentAt = entity.SentAt,
        ValidUntil = entity.ValidUntil,
        Notes = entity.Notes,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt
    };
}
