using System.Text.Json;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Integrations;

internal static class IntegrationRecoveryAudit
{
    internal const string Action = "process-recorded-evidence";
    internal static ActivityEntry Entry(Guid workspace, Guid user, Guid operation, string outcome, DateTimeOffset now) => new()
    {
        WorkspaceId = workspace, EntityTypeCode = "integration-operation", EntityId = operation,
        EventTypeCode = "integration.recovery", ActorTypeCode = "user", ActorUserId = user, OccurredAt = now,
        // Preserve the operator identifier even if ActorUserId is later nulled by its existing FK.
        MetadataJson = JsonSerializer.Serialize(new { operatorId = user, operationId = operation, action = Action, outcome })
    };
}
