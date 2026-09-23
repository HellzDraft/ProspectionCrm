using ProspectionCrm.Api.Dtos.EmailMessages;

namespace ProspectionCrm.Api.Services;

public interface IEmailMessageService
{
    Task<(IReadOnlyList<EmailMessageDto>? Messages, string? Error)> GetAllAsync(
        Guid? opportunityId = null, Guid? companyId = null, Guid? contactId = null,
        string? directionCode = null, DateTimeOffset? from = null, DateTimeOffset? to = null,
        CancellationToken cancellationToken = default);
    Task<EmailMessageDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}
