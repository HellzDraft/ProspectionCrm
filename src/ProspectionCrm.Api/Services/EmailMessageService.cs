using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.EmailMessages;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class EmailMessageService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider)
    : IEmailMessageService
{
    public async Task<(IReadOnlyList<EmailMessageDto>? Messages, string? Error)> GetAllAsync(
        Guid? opportunityId = null, Guid? companyId = null, Guid? contactId = null,
        string? directionCode = null, DateTimeOffset? from = null, DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        if (directionCode is not (null or "inbound" or "outbound"))
            return (null, "DirectionCode must be inbound or outbound when provided.");
        from = from?.ToUniversalTime();
        to = to?.ToUniversalTime();
        if (from.HasValue && to.HasValue && from.Value > to.Value)
            return (null, "The 'from' date must be less than or equal to the 'to' date.");

        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var messages = await dbContext.EmailMessages.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId
                && (!opportunityId.HasValue || x.OpportunityId == opportunityId.Value)
                && (!companyId.HasValue || x.CompanyId == companyId.Value)
                && (!contactId.HasValue || x.ContactId == contactId.Value)
                && (directionCode == null || x.DirectionCode == directionCode)
                && (!from.HasValue || x.OccurredAt >= from.Value)
                && (!to.HasValue || x.OccurredAt <= to.Value))
            .OrderByDescending(x => x.OccurredAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return (messages.Select(ToDto).ToList(), null);
    }

    public async Task<EmailMessageDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var message = await dbContext.EmailMessages.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return message is null ? null : ToDto(message);
    }

    // Future provider ingestion must validate that all CRM references belong to this workspace.
    private static EmailMessageDto ToDto(EmailMessage message) => new()
    {
        Id = message.Id,
        OpportunityId = message.OpportunityId,
        CompanyId = message.CompanyId,
        ContactId = message.ContactId,
        ProviderCode = message.ProviderCode,
        ExternalMessageId = message.ExternalMessageId,
        ExternalThreadId = message.ExternalThreadId,
        DirectionCode = message.DirectionCode,
        FromAddress = message.FromAddress,
        ToAddressesJson = message.ToAddressesJson,
        CcAddressesJson = message.CcAddressesJson,
        Subject = message.Subject,
        BodyText = message.BodyText,
        OccurredAt = message.OccurredAt,
        CreatedAt = message.CreatedAt
    };
}
