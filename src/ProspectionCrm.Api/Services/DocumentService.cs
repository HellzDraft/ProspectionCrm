using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProspectionCrm.Api.Data;
using ProspectionCrm.Api.Dtos.Documents;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services;

public class DocumentService(ProspectionCrmDbContext dbContext, ICurrentWorkspaceProvider currentWorkspaceProvider) : IDocumentService
{
    public async Task<IReadOnlyList<DocumentDto>> GetAllAsync(bool includeArchived = false, string? kindCode = null,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var documents = await dbContext.Documents.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && (includeArchived || x.ArchivedAt == null)
                && (kindCode == null || x.KindCode == kindCode))
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return documents.Select(ToDto).ToList();
    }

    public async Task<DocumentDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var document = await dbContext.Documents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        return document is null ? null : ToDto(document);
    }

    public async Task<(DocumentDto? Document, string? Error)> CreateAsync(CreateDocumentRequest request, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (request.SizeBytes < 0)
            return (null, "SizeBytes must be greater than or equal to zero.");
        if (request.Sha256 is not null && (request.Sha256.Length != 64 || request.Sha256.Any(c => !Uri.IsHexDigit(c))))
            return (null, "Sha256 must contain exactly 64 hexadecimal characters when provided.");
        if (await dbContext.Documents.AnyAsync(x => x.WorkspaceId == workspaceId && x.StorageKey == request.StorageKey, cancellationToken))
            return (null, "This storage key is already used by a document in the current workspace.");
        var document = new Document
        {
            WorkspaceId = workspaceId,
            KindCode = request.KindCode,
            OriginalFileName = request.OriginalFileName,
            StorageKey = request.StorageKey,
            ContentType = request.ContentType,
            SizeBytes = request.SizeBytes,
            Sha256 = request.Sha256?.ToLowerInvariant()
        };
        dbContext.Documents.Add(document);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Documents_WorkspaceId_StorageKey" })
        {
            dbContext.Entry(document).State = EntityState.Detached;
            return (null, "This storage key is already used by a document in the current workspace.");
        }
        return (ToDto(document), null);
    }

    public async Task<(bool Found, string? Error)> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var document = await dbContext.Documents.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (document is null)
            return (false, null);
        if (await dbContext.CandidateProfiles.AnyAsync(x => x.WorkspaceId == workspaceId
                && x.ArchivedAt == null && x.PrimaryCvDocumentId == id, cancellationToken))
            return (true, "This document is the primary CV of an active candidate profile and cannot be archived.");
        document.ArchivedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await currentWorkspaceProvider.GetCurrentWorkspaceIdAsync(cancellationToken);
        var document = await dbContext.Documents.SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == workspaceId, cancellationToken);
        if (document is null)
            return false;
        document.ArchivedAt = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static DocumentDto ToDto(Document document) => new()
    {
        Id = document.Id,
        KindCode = document.KindCode,
        OriginalFileName = document.OriginalFileName,
        StorageKey = document.StorageKey,
        ContentType = document.ContentType,
        SizeBytes = document.SizeBytes,
        Sha256 = document.Sha256,
        CreatedAt = document.CreatedAt,
        ArchivedAt = document.ArchivedAt
    };
}
