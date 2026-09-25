using ProspectionCrm.Api.Dtos.Documents;

namespace ProspectionCrm.Api.Services;

public interface IDocumentService
{
    Task<IReadOnlyList<DocumentDto>> GetAllAsync(bool includeArchived = false, string? kindCode = null,
        CancellationToken cancellationToken = default);
    Task<DocumentDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(DocumentDto? Document, string? Error)> CreateAsync(CreateDocumentRequest request, CancellationToken cancellationToken);
    Task<(bool Found, string? Error)> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken);
}
