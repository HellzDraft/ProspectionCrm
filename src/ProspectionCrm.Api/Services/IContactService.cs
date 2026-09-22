using ProspectionCrm.Api.Dtos.Contacts;

namespace ProspectionCrm.Api.Services;

public interface IContactService
{
    Task<IReadOnlyList<ContactDto>> GetAllAsync(CancellationToken cancellationToken);
    Task<ContactDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(ContactDto? Contact, string? Error)> CreateAsync(
        CreateContactRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> UpdateAsync(
        Guid id, UpdateContactRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
