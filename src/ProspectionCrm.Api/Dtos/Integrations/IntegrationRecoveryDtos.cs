using System.Text.Json.Serialization;

namespace ProspectionCrm.Api.Dtos.Integrations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntegrationRecoveryConfirmation(
    [property: JsonRequired] Guid OperationId, [property: JsonRequired] Guid BusinessId,
    [property: JsonRequired] string Type, [property: JsonRequired] string BusinessStatus,
    [property: JsonRequired] long Revision, [property: JsonRequired] IReadOnlyList<Guid> ReceiptIds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IntegrationRecoveryRequest(
    [property: JsonRequired] string Action, [property: JsonRequired] long ExpectedRevision,
    [property: JsonRequired] IntegrationRecoveryConfirmation Confirmation);

public sealed record IntegrationRecoveryResponse(Guid OperationId, string OutcomeCode, string DiagnosticUrl);
