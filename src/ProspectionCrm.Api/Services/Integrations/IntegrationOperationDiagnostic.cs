using System.Data;
using Microsoft.EntityFrameworkCore;
using ProspectionCrm.Api.Dtos.Integrations;

namespace ProspectionCrm.Api.Services.Integrations;

public sealed partial class IntegrationSupervisionService
{
    public async Task<IntegrationOperationDiagnostic?> DiagnosticAsync(Guid operationId, int attemptOffset,
        int receiptOffset, int limit, CancellationToken token)
    {
        var workspaceId = await workspace.GetCurrentWorkspaceIdAsync(token);
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
        var op = await Query(workspaceId, now).SingleOrDefaultAsync(x => x.Id == operationId, token);
        if (op is null) return null; // Do not query child metadata for a foreign operation.
        var attempts = await db.IntegrationAttempts.AsNoTracking().Where(x => x.OperationId == op.Id)
            .OrderBy(x => x.Number).Skip(attemptOffset).Take(limit)
            .Select(x => new IntegrationAttemptMetadata(x.Id, x.Number, x.StatusCode, x.StartedAt, x.FinishedAt)).ToListAsync(token);
        var receiptsQuery = db.IntegrationReceipts.AsNoTracking().Where(x => x.OperationId == op.Id);
        var receiptCount = await receiptsQuery.LongCountAsync(token);
        var receipts = await receiptsQuery.OrderBy(x => x.EvidenceNumber).ThenBy(x => x.MessageId)
            .Skip(receiptOffset).Take(limit)
            .Select(x => new IntegrationReceiptMetadata(x.MessageId, x.EvidenceNumber, x.ReceivedAt, x.ProcessedAt)).ToListAsync(token);
        // Only a persisted reconciliation diagnosis is authoritative about proof content.
        var proofDiagnostic = await db.CalendarEventDeliveries.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.IntegrationOperationId == op.Id
                && x.ConnectionId == op.ConnectionId && x.OperationType == op.Type)
            .Select(x => x.DiagnosticCode == "CalendarEvidenceContradiction" ? "contradictory"
                : x.DiagnosticCode == "CalendarProviderProofInvalid" || x.DiagnosticCode == "CalendarConflictProofInvalid"
                || x.DiagnosticCode == "CalendarConfirmedContentMismatch" || x.DiagnosticCode == "CalendarEvidenceContextMismatch"
                    ? "inconsistent" : null).SingleOrDefaultAsync(token);
        // Email's no-proof recovery branch needs no payload or approval interpretation.
        // Use the database clock, as EmailSendDeliveryService.ReconcileAsync does.
        var emailRecovery = false;
        if (op.Type == "email.send" && receiptCount == 0 && op.BusinessStatusCode is "dispatching" or "awaiting-result")
        {
            var databaseNow = await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(token);
            emailRecovery = await db.EmailSendDeliveries.AsNoTracking().AnyAsync(x => x.Id == op.BusinessId
                && x.WorkspaceId == workspaceId && x.IntegrationOperationId == op.Id && x.ConnectionId == op.ConnectionId
                && (x.StatusCode == "dispatching" || x.StatusCode == "awaiting-result")
                && x.StartedAt != null && x.StartedAt <= databaseNow.AddMinutes(-2)
                && db.EmailAccounts.Any(a => a.Id == x.EmailAccountId && a.WorkspaceId == workspaceId), token);
        }
        await tx.CommitAsync(token);

        var anomalies = new List<string>();
        if (op.OldActiveAttemptCount > 0) anomalies.Add("IntegrationAttemptOld");
        if (op.WaitingSince <= now.AddMinutes(-OldWaitingMinutes)) anomalies.Add("IntegrationWaitingOld");
        if (op.OldUnprocessedReceiptCount > 0) anomalies.Add("IntegrationEvidenceOld");
        if (op.UnprocessedReceiptCount > 0 && op.CompletedAt is not null) anomalies.Add("EvidencePendingAfterCompletion");
        var code = proofDiagnostic switch
        {
            "contradictory" => "EvidenceContradictory",
            "inconsistent" => "EvidenceInconsistent",
            _ => op.State switch
            {
                "conflict" => "BusinessConflict",
                "evidence-pending" => "EvidencePending",
                "uncertain" => receiptCount == 0 ? "UncertainWithoutEvidence" : "UncertainAfterEvidence",
                "failed" => "OperationFailed",
                "completed" => "OperationCompleted",
                "diagnostic-required" => "MetadataInconsistent",
                _ => "NormalWaiting"
            }
        };
        if (code is not ("NormalWaiting" or "OperationCompleted")) anomalies.Add(code);
        var risk = code switch
        {
            "EvidenceContradictory" or "EvidenceInconsistent" or "BusinessConflict" or "MetadataInconsistent" => "high",
            "OperationCompleted" when anomalies.Count == 0 => "none",
            "NormalWaiting" when anomalies.Count == 0 => "low",
            _ => "medium"
        };
        var recommendations = new List<string>();
        if (op.UnprocessedReceiptCount > 0) recommendations.Add("ExamineExistingEvidence");
        if (code is "NormalWaiting" or "UncertainWithoutEvidence" or "UncertainAfterEvidence") recommendations.Add("WaitForEvidence");
        if (risk == "high" || code is "OperationFailed" or "UncertainWithoutEvidence" or "UncertainAfterEvidence"
            || anomalies.Contains("IntegrationAttemptOld") || anomalies.Contains("IntegrationWaitingOld"))
            recommendations.Add("ManualInvestigationRequired");
        if (code == "OperationCompleted" && anomalies.Count == 0) recommendations.Add("NoInterventionRequired");

        // Fail closed when payload, approval or context validation would be needed.
        var route = op.Type switch
        {
            "email.send" => "/api/email-send-deliveries/{id}/reconcile",
            "calendar.event.create" or "calendar.event.update" => "/api/calendar-event-deliveries/{id}/reconcile",
            "collection.request" => "/api/source-collection-jobs/{id}/process-result",
            _ => null
        };
        var review = route is not null && op.BusinessId is not null && op.State is "evidence-pending" or "uncertain"
            && (op.Type != "collection.request" || op.BusinessStatusCode == "result-received" && op.StatusCode == "result-received");
        if (review) recommendations.Add("ReviewHumanReconciliationPreconditions");
        if (emailRecovery) recommendations.Add("HumanReconciliationAvailable");
        return new(now, op, code, risk, proofDiagnostic ?? (receiptCount == 0 ? "no-evidence" : "not-assessed"),
            anomalies, recommendations, new(emailRecovery ? "available-at-observation" : review ? "requires-validation" : "not-recommended",
                review || emailRecovery ? route : null, review || emailRecovery ? op.BusinessId : null,
                emailRecovery ? "EmailAttemptRecoveryPreconditionsVerified"
                    : review ? "ProofAndBusinessPreconditionsNotVerified" : "NoVerifiedReconciliationAction"),
            new(attemptOffset, limit, op.AttemptCount, attempts), new(receiptOffset, limit, receiptCount, receipts));
    }
}
