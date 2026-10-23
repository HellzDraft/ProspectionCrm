namespace ProspectionCrm.Api.Services.Integrations;

internal static class IntegrationSupervisionQuery
{
    // All joins include workspace AND operation binding. Never select a payload or a free-text error.
    internal const string Sql = """
        WITH metadata AS (
          SELECT o."Id", o."ConnectionId", o."CommandMessageId", o."CorrelationId", o."Type", o."StatusCode",
            o."CreatedAt", o."CompletedAt", b.id AS "BusinessId", b.status AS "BusinessStatusCode",
            b.diagnostic IS NOT NULL AS "HasRecordedDiagnostic",
            CASE WHEN b.diagnostic IN ('EmailMessageContentConflict','EmailCursorConflict','CalendarEventContentConflict',
              'CalendarCursorConflict','EmailSendUnknown','EmailDefinitelyNotSent','RemoteDispatchUnknown',
              'EmailDispatchUnknown','CalendarDispatchUnknown','CalendarOperationUnknown','CalendarProviderVersionConflict',
              'CalendarEvidenceContradiction','CalendarProviderNotApplied','CalendarProviderProofInvalid',
              'EmailProviderFailed','CalendarProviderFailed','EmailResultInvalid','CalendarResultInvalid',
              'AmbiguousIdentity','ConcurrentIdentityChange','MissingPersistentIdentity','RemoteContextChanged')
              THEN b.diagnostic WHEN b.diagnostic IS NOT NULL THEN 'RecordedDiagnostic' ELSE NULL END AS "DiagnosticCode",
            last."Id" AS "LatestAttemptId", last."Number" AS "LatestAttemptNumber",
            last."StatusCode" AS "LatestAttemptStatusCode", last."StartedAt" AS "LatestAttemptStartedAt",
            a.total AS "AttemptCount", a.active AS "ActiveAttemptCount", a.old AS "OldActiveAttemptCount",
            a.oldest AS "OldestActiveAttemptAt", r.total AS "UnprocessedReceiptCount",
            r.oldest AS "OldestUnprocessedReceiptAt", r.old AS "OldUnprocessedReceiptCount"
          FROM "IntegrationOperations" o
          LEFT JOIN LATERAL (
            SELECT j."Id" AS id, j."StatusCode" AS status, j."ErrorCode" AS diagnostic
              FROM "RemoteCollectionIntents" i JOIN "SourceCollectionJobs" j
                ON j."Id" = i."JobId" AND j."WorkspaceId" = i."WorkspaceId"
              WHERE i."OperationId" = o."Id" AND i."WorkspaceId" = o."WorkspaceId"
                AND i."ConnectionId" = o."ConnectionId" AND o."Type" = 'collection.request'
            UNION ALL
            SELECT j."Id", j."StatusCode", j."ErrorCode" FROM "EmailSyncJobs" j
              WHERE j."IntegrationOperationId" = o."Id" AND j."WorkspaceId" = o."WorkspaceId"
                AND j."ConnectionId" = o."ConnectionId" AND o."Type" = 'email.sync'
            UNION ALL
            SELECT j."Id", j."StatusCode", j."ErrorCode" FROM "CalendarSyncJobs" j
              WHERE j."IntegrationOperationId" = o."Id" AND j."WorkspaceId" = o."WorkspaceId"
                AND j."ConnectionId" = o."ConnectionId" AND o."Type" = 'calendar.sync'
            UNION ALL
            SELECT j."Id", j."StatusCode", j."DiagnosticCode" FROM "EmailSendDeliveries" j
              WHERE j."IntegrationOperationId" = o."Id" AND j."WorkspaceId" = o."WorkspaceId"
                AND j."ConnectionId" = o."ConnectionId" AND o."Type" = 'email.send'
            UNION ALL
            SELECT j."Id", j."StatusCode", j."DiagnosticCode" FROM "CalendarEventDeliveries" j
              WHERE j."IntegrationOperationId" = o."Id" AND j."WorkspaceId" = o."WorkspaceId"
                AND j."ConnectionId" = o."ConnectionId" AND j."OperationType" = o."Type"
          ) b ON true
          LEFT JOIN LATERAL (
            SELECT t."Id", t."Number", t."StatusCode", t."StartedAt" FROM "IntegrationAttempts" t
              WHERE t."OperationId" = o."Id" ORDER BY t."Number" DESC LIMIT 1
          ) last ON true
          CROSS JOIN LATERAL (
            SELECT count(*) AS total, count(*) FILTER (WHERE t."StatusCode" = 'started') AS active,
              count(*) FILTER (WHERE t."StatusCode" = 'started' AND t."StartedAt" <= @oldAttempt) AS old,
              min(t."StartedAt") FILTER (WHERE t."StatusCode" = 'started') AS oldest
              FROM "IntegrationAttempts" t WHERE t."OperationId" = o."Id"
          ) a
          CROSS JOIN LATERAL (
            SELECT count(*) AS total, min(t."ReceivedAt") AS oldest,
              count(*) FILTER (WHERE t."ReceivedAt" <= @oldWaiting) AS old FROM "IntegrationReceipts" t
              WHERE t."OperationId" = o."Id" AND t."ProcessedAt" IS NULL
          ) r
          WHERE o."WorkspaceId" = @workspace
        ), classified AS (
          SELECT metadata.*, CASE
            WHEN "BusinessStatusCode" = 'conflict' OR "DiagnosticCode" IN ('EmailMessageContentConflict','EmailCursorConflict',
              'CalendarEventContentConflict','CalendarCursorConflict','AmbiguousIdentity','ConcurrentIdentityChange',
              'MissingPersistentIdentity','RemoteContextChanged') THEN 'conflict'
            WHEN "UnprocessedReceiptCount" > 0 THEN 'evidence-pending'
            WHEN "BusinessStatusCode" IN ('dispatch-unknown','send-unknown','operation-unknown') THEN 'uncertain'
            WHEN "BusinessStatusCode" = 'failed' THEN 'failed'
            WHEN "BusinessStatusCode" IN ('succeeded','sent','cancelled') THEN 'completed'
            WHEN "StatusCode" = 'failed' THEN 'failed'
            WHEN "StatusCode" = 'succeeded' THEN 'completed'
            WHEN "Type" <> 'technical.probe' AND "BusinessId" IS NULL THEN 'diagnostic-required'
            WHEN "LatestAttemptStatusCode" = 'unknown' THEN 'uncertain'
            WHEN "LatestAttemptStatusCode" = 'failed' THEN 'failed'
            WHEN "StatusCode" = 'result-received' OR "BusinessStatusCode" = 'result-received' THEN 'diagnostic-required'
            ELSE 'waiting' END AS "State"
          FROM metadata
        )
        SELECT classified.*, CASE WHEN "State" = 'waiting'
          THEN COALESCE("LatestAttemptStartedAt", "CreatedAt") ELSE NULL END AS "WaitingSince",
          CASE WHEN "State" = 'uncertain' THEN COALESCE("LatestAttemptStartedAt", "CreatedAt") ELSE NULL END AS "UncertainSince"
        FROM classified
        """;
}
