namespace ProspectionCrm.Api.Dtos.Integrations;

public sealed record IntegrationAttemptMetadata(Guid Id, int Number, string StatusCode,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);
public sealed record IntegrationReceiptMetadata(Guid MessageId, int EvidenceNumber,
    DateTimeOffset ReceivedAt, DateTimeOffset? ProcessedAt);
public sealed record IntegrationDiagnosticPage<T>(int Offset, int Limit, long Total, IReadOnlyList<T> Items);
// Only available-at-observation indicates verified preconditions; the POST always rechecks them.
public sealed record IntegrationReconciliationReview(string StatusCode, string? RouteTemplate,
    Guid? ResourceId, string ReasonCode);
public sealed record IntegrationOperationDiagnostic(DateTimeOffset ObservedAt, IntegrationOperationView Operation,
    string DiagnosticCode, string RiskLevel, string EvidenceAssessment,
    IReadOnlyList<string> AnomalyCodes, IReadOnlyList<string> RecommendationCodes,
    IntegrationReconciliationReview Reconciliation,
    IntegrationDiagnosticPage<IntegrationAttemptMetadata> Attempts,
    IntegrationDiagnosticPage<IntegrationReceiptMetadata> Receipts);
