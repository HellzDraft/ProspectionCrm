using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Data;

namespace ProspectionCrm.Api.Services;

internal static class ProfessionalReferenceValidation
{
    internal static async Task<string?> DocumentAsync(ProspectionCrmDbContext dbContext, Guid workspaceId,
        Guid? documentId, string kindCode, bool requireActive, string fieldName, CancellationToken cancellationToken)
    {
        if (documentId.HasValue && !await dbContext.Documents.AnyAsync(x => x.Id == documentId.Value
                && x.WorkspaceId == workspaceId && x.KindCode == kindCode
                && (!requireActive || x.ArchivedAt == null), cancellationToken))
            return $"{fieldName} must reference a {kindCode} document in the current workspace, active for a new selection or restore.";
        return null;
    }

    internal static async Task<string?> ProfileAsync(ProspectionCrmDbContext dbContext, Guid workspaceId,
        Guid? profileId, bool requireActive, CancellationToken cancellationToken)
    {
        if (profileId.HasValue && !await dbContext.CandidateProfiles.AnyAsync(x => x.Id == profileId.Value
                && x.WorkspaceId == workspaceId && (!requireActive || x.ArchivedAt == null), cancellationToken))
            return "CandidateProfileId must reference a profile in the current workspace, active for a new selection.";
        return null;
    }
}
