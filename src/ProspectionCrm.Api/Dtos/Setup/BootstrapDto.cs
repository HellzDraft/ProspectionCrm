namespace ProspectionCrm.Api.Dtos.Setup;

public sealed record BootstrapDto(
    Guid OwnerUserId,
    string OwnerEmail,
    string? OwnerDisplayName,
    Guid WorkspaceId,
    string WorkspaceName,
    string TimeZoneId);
