using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase9641CalendarEventDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CalendarEventPlans_WorkspaceId_Id",
                table: "CalendarEventPlans",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateTable(
                name: "CalendarEventDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CalendarEventPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationActionRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    CalendarAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CalendarId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationOperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalCalendarId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TargetCalendarEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetExternalEventId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TargetExternalRevision = table.Column<long>(type: "bigint", nullable: true),
                    TargetContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StatusCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExternalEventId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExternalRevision = table.Column<long>(type: "bigint", nullable: true),
                    CalendarEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProofHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DiagnosticCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventDeliveries", x => x.Id);
                    table.CheckConstraint("CK_CalendarDelivery_Dates", "(\"StartedAt\" IS NULL OR \"StartedAt\" >= \"CreatedAt\") AND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\")");
                    table.CheckConstraint("CK_CalendarDelivery_Identity", "\"ContentHash\" ~ '^[0-9a-f]{64}$' AND \"IdempotencyKey\" = 'calendar-event-' || \"Id\"::text");
                    table.CheckConstraint("CK_CalendarDelivery_State", "\"StatusCode\" IN ('pending','dispatching','awaiting-result','operation-unknown','result-received','succeeded','failed','conflict')\nAND (\"StatusCode\" = 'pending') = (\"StartedAt\" IS NULL)\nAND (\"StatusCode\" IN ('succeeded','failed','conflict')) = (\"FinishedAt\" IS NOT NULL)\nAND (\"StatusCode\" <> 'succeeded' OR (\"CalendarEventId\" IS NOT NULL AND \"ExternalEventId\" IS NOT NULL AND \"ExternalRevision\" IS NOT NULL AND \"ProofHash\" IS NOT NULL))\nAND (\"ExternalRevision\" IS NULL OR \"ExternalRevision\" >= 0)\nAND (\"ProofHash\" IS NULL OR \"ProofHash\" ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("CK_CalendarDelivery_Target", "(\"OperationType\" = 'calendar.event.create' AND \"TargetCalendarEventId\" IS NULL AND \"TargetExternalEventId\" IS NULL\n    AND \"TargetExternalRevision\" IS NULL AND \"TargetContentHash\" IS NULL)\nOR (\"OperationType\" = 'calendar.event.update' AND \"TargetCalendarEventId\" IS NOT NULL AND \"TargetExternalEventId\" IS NOT NULL\n    AND \"TargetExternalRevision\" IS NOT NULL AND \"TargetExternalRevision\" >= 0 AND \"TargetContentHash\" IS NOT NULL AND \"TargetContentHash\" ~ '^[0-9a-f]{64}$')");
                    table.ForeignKey(
                        name: "FK_CalendarEventDeliveries_AutomationActionRequests_WorkspaceI~",
                        columns: x => new { x.WorkspaceId, x.AutomationActionRequestId },
                        principalTable: "AutomationActionRequests",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEventDeliveries_CalendarEventPlans_WorkspaceId_Cale~",
                        columns: x => new { x.WorkspaceId, x.CalendarEventPlanId },
                        principalTable: "CalendarEventPlans",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEventDeliveries_CalendarEvents_WorkspaceId_Calendar~",
                        columns: x => new { x.WorkspaceId, x.CalendarEventId },
                        principalTable: "CalendarEvents",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEventDeliveries_CalendarEvents_WorkspaceId_TargetCa~",
                        columns: x => new { x.WorkspaceId, x.TargetCalendarEventId },
                        principalTable: "CalendarEvents",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEventDeliveries_Calendars_WorkspaceId_CalendarId_Ca~",
                        columns: x => new { x.WorkspaceId, x.CalendarId, x.CalendarAccountId, x.ConnectionId },
                        principalTable: "Calendars",
                        principalColumns: new[] { "WorkspaceId", "Id", "CalendarAccountId", "ConnectionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEventDeliveries_IntegrationOperations_IntegrationOp~",
                        column: x => x.IntegrationOperationId,
                        principalTable: "IntegrationOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations",
                sql: "\"Version\" = 1 AND ((\"Type\" = 'technical.probe' AND \"ProbeId\" <> '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"Type\" IN ('collection.request','email.sync','email.send','calendar.sync','calendar.event.create','calendar.event.update') AND \"ConnectionId\" IS NOT NULL AND \"ProbeId\" = '00000000-0000-0000-0000-000000000000'::uuid))");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventDeliveries_AutomationActionRequestId",
                table: "CalendarEventDeliveries",
                column: "AutomationActionRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventDeliveries_CalendarEventPlanId",
                table: "CalendarEventDeliveries",
                column: "CalendarEventPlanId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventDeliveries_IdempotencyKey",
                table: "CalendarEventDeliveries",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventDeliveries_IntegrationOperationId",
                table: "CalendarEventDeliveries",
                column: "IntegrationOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventDeliveries_WorkspaceId_AutomationActionRequest~",
                table: "CalendarEventDeliveries",
                columns: new[] { "WorkspaceId", "AutomationActionRequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventDeliveries_WorkspaceId_CalendarEventId",
                table: "CalendarEventDeliveries",
                columns: new[] { "WorkspaceId", "CalendarEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventDeliveries_WorkspaceId_CalendarEventPlanId",
                table: "CalendarEventDeliveries",
                columns: new[] { "WorkspaceId", "CalendarEventPlanId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventDeliveries_WorkspaceId_CalendarId_CalendarAcco~",
                table: "CalendarEventDeliveries",
                columns: new[] { "WorkspaceId", "CalendarId", "CalendarAccountId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventDeliveries_WorkspaceId_StatusCode_CreatedAt",
                table: "CalendarEventDeliveries",
                columns: new[] { "WorkspaceId", "StatusCode", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventDeliveries_WorkspaceId_TargetCalendarEventId",
                table: "CalendarEventDeliveries",
                columns: new[] { "WorkspaceId", "TargetCalendarEventId" });
            migrationBuilder.Sql("""
                ALTER TABLE "CalendarEventDeliveries" ADD CONSTRAINT "FK_CalendarDelivery_OperationBinding"
                FOREIGN KEY ("WorkspaceId","ConnectionId","IntegrationOperationId","OperationType")
                REFERENCES "IntegrationOperations" ("WorkspaceId","ConnectionId","Id","Type") ON DELETE RESTRICT;
                CREATE FUNCTION calendar_delivery_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Calendar delivery history cannot be deleted' USING ERRCODE = '23514';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."StatusCode" <> 'pending' OR NOT EXISTS (
                            SELECT 1 FROM "CalendarEventPlans" p JOIN "Calendars" c ON c."Id"=p."CalendarId"
                            JOIN "AutomationActionRequests" r ON r."Id"=p."AutomationActionRequestId"
                            WHERE p."Id"=NEW."CalendarEventPlanId" AND p."WorkspaceId"=NEW."WorkspaceId"
                            AND p."StatusCode"='approved' AND r."StatusCode"='approved' AND r."WorkspaceId"=NEW."WorkspaceId"
                            AND r."DecidedAt" IS NOT NULL AND r."ActionCategoryCode"='calendar' AND r."ActionTypeCode"=NEW."OperationType"
                            AND p."AutomationActionRequestId"=NEW."AutomationActionRequestId"
                            AND p."CalendarAccountId"=NEW."CalendarAccountId" AND p."CalendarId"=NEW."CalendarId"
                            AND p."ConnectionId"=NEW."ConnectionId" AND c."ProviderCode"=NEW."ProviderCode"
                            AND c."ExternalCalendarId"=NEW."ExternalCalendarId" AND p."ContentHash"=NEW."ContentHash"
                            AND r."ContentFingerprint"=NEW."ContentHash" AND NEW."OperationType"='calendar.event.' || p."OperationCode"
                            AND p."TargetCalendarEventId" IS NOT DISTINCT FROM NEW."TargetCalendarEventId"
                            AND p."TargetExternalEventId" IS NOT DISTINCT FROM NEW."TargetExternalEventId"
                            AND p."TargetExternalRevision" IS NOT DISTINCT FROM NEW."TargetExternalRevision"
                            AND p."TargetSnapshotJson"->>'contentHash' IS NOT DISTINCT FROM NEW."TargetContentHash") THEN
                            RAISE EXCEPTION 'Calendar delivery binding invalid' USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        IF (to_jsonb(NEW) - ARRAY['StatusCode','StartedAt','FinishedAt','ExternalEventId','ExternalRevision','CalendarEventId','ProofHash','DiagnosticCode'])
                            IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['StatusCode','StartedAt','FinishedAt','ExternalEventId','ExternalRevision','CalendarEventId','ProofHash','DiagnosticCode'])
                            OR (OLD."StartedAt" IS NOT NULL AND NEW."StartedAt" IS DISTINCT FROM OLD."StartedAt")
                            OR (OLD."FinishedAt" IS NOT NULL AND NEW."FinishedAt" IS DISTINCT FROM OLD."FinishedAt")
                            OR (OLD."ProofHash" IS NOT NULL AND NEW."ProofHash" IS DISTINCT FROM OLD."ProofHash")
                            OR (OLD."ExternalEventId" IS NOT NULL AND (NEW."ExternalEventId",NEW."ExternalRevision") IS DISTINCT FROM (OLD."ExternalEventId",OLD."ExternalRevision"))
                            OR (OLD."CalendarEventId" IS NOT NULL AND NEW."CalendarEventId" IS DISTINCT FROM OLD."CalendarEventId")
                            OR (OLD."StatusCode" <> 'pending' AND NEW."StatusCode" = 'pending')
                            OR (OLD."StatusCode" <> 'pending' AND OLD."StatusCode" <> 'dispatching' AND NEW."StatusCode"='dispatching')
                            OR (OLD."StatusCode" IN ('succeeded','failed') AND NEW."StatusCode" NOT IN (OLD."StatusCode",'conflict'))
                            OR (OLD."StatusCode"='conflict' AND NEW IS DISTINCT FROM OLD) THEN
                            RAISE EXCEPTION 'Calendar delivery identity, proof or state is immutable' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER "TR_CalendarDelivery_Guard" BEFORE INSERT OR UPDATE OR DELETE ON "CalendarEventDeliveries"
                    FOR EACH ROW EXECUTE FUNCTION calendar_delivery_guard();
                CREATE FUNCTION calendar_operation_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."Type" IN ('calendar.event.create','calendar.event.update') THEN
                        IF TG_OP='DELETE' OR (to_jsonb(NEW)-ARRAY['StatusCode','Revision','CompletedAt'])
                            IS DISTINCT FROM (to_jsonb(OLD)-ARRAY['StatusCode','Revision','CompletedAt'])
                            OR (OLD."StatusCode"<>'pending' AND NEW."StatusCode"='pending') THEN
                            RAISE EXCEPTION 'Calendar operation identity is immutable' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
                END $$;
                CREATE TRIGGER "TR_CalendarOperation_Guard" BEFORE UPDATE OR DELETE ON "IntegrationOperations"
                    FOR EACH ROW EXECUTE FUNCTION calendar_operation_guard();
                CREATE FUNCTION calendar_attempt_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE op uuid;
                BEGIN
                    op := CASE WHEN TG_OP='INSERT' THEN NEW."OperationId" ELSE OLD."OperationId" END;
                    IF EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE ("Id"=op OR "Id"=NEW."OperationId") AND "Type" IN ('calendar.event.create','calendar.event.update')) THEN
                        IF TG_OP='DELETE' OR NEW."Number" <> 1
                            OR (TG_OP='UPDATE' AND (NEW."Id",NEW."OperationId",NEW."Number",NEW."StartedAt") IS DISTINCT FROM (OLD."Id",OLD."OperationId",OLD."Number",OLD."StartedAt")) THEN
                            RAISE EXCEPTION 'Calendar attempt cannot be retried or erased' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
                END $$;
                CREATE TRIGGER "TR_CalendarAttempt_Guard" BEFORE INSERT OR UPDATE OR DELETE ON "IntegrationAttempts"
                    FOR EACH ROW EXECUTE FUNCTION calendar_attempt_guard();
                CREATE FUNCTION calendar_receipt_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE "Id"=OLD."OperationId" AND "Type" IN ('calendar.event.create','calendar.event.update'))
                        AND (TG_OP='DELETE' OR (to_jsonb(NEW)-'ProcessedAt') IS DISTINCT FROM (to_jsonb(OLD)-'ProcessedAt')
                            OR (OLD."ProcessedAt" IS NOT NULL AND NEW."ProcessedAt" IS DISTINCT FROM OLD."ProcessedAt")) THEN
                        RAISE EXCEPTION 'Calendar evidence is immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
                END $$;
                CREATE TRIGGER "TR_CalendarReceipt_Guard" BEFORE UPDATE OR DELETE ON "IntegrationReceipts"
                    FOR EACH ROW EXECUTE FUNCTION calendar_receipt_guard();
                CREATE OR REPLACE FUNCTION email_receipt_slot_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE "Id"=NEW."OperationId")
                    AND NOT EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE "Id"=NEW."OperationId"
                        AND (("Type" IN ('email.send','calendar.event.create','calendar.event.update') AND NEW."EvidenceNumber" BETWEEN 1 AND 100)
                            OR ("Type" NOT IN ('email.send','calendar.event.create','calendar.event.update') AND NEW."EvidenceNumber"=0))) THEN
                        RAISE EXCEPTION 'Invalid integration evidence slot' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "CalendarEventDeliveries") OR EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE "Type" IN ('calendar.event.create','calendar.event.update')) THEN
                        RAISE EXCEPTION 'Phase9641 downgrade requires an empty calendar execution history';
                    END IF;
                END $$;
                DROP TRIGGER "TR_CalendarDelivery_Guard" ON "CalendarEventDeliveries";
                DROP FUNCTION calendar_delivery_guard();
                DROP TRIGGER "TR_CalendarOperation_Guard" ON "IntegrationOperations";
                DROP FUNCTION calendar_operation_guard();
                DROP TRIGGER "TR_CalendarAttempt_Guard" ON "IntegrationAttempts";
                DROP FUNCTION calendar_attempt_guard();
                DROP TRIGGER "TR_CalendarReceipt_Guard" ON "IntegrationReceipts";
                DROP FUNCTION calendar_receipt_guard();
                CREATE OR REPLACE FUNCTION email_receipt_slot_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE "Id"=NEW."OperationId")
                    AND NOT EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE "Id"=NEW."OperationId"
                        AND (("Type"='email.send' AND NEW."EvidenceNumber" BETWEEN 1 AND 100)
                            OR ("Type"<>'email.send' AND NEW."EvidenceNumber"=0))) THEN
                        RAISE EXCEPTION 'Invalid integration evidence slot' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "CalendarEventDeliveries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CalendarEventPlans_WorkspaceId_Id",
                table: "CalendarEventPlans");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations",
                sql: "\"Version\" = 1 AND ((\"Type\" = 'technical.probe' AND \"ProbeId\" <> '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"Type\" IN ('collection.request','email.sync','email.send','calendar.sync') AND \"ConnectionId\" IS NOT NULL AND \"ProbeId\" = '00000000-0000-0000-0000-000000000000'::uuid))");
        }
    }
}
