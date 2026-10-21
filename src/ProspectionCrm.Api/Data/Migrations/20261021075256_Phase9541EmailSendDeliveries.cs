using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase9541EmailSendDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IntegrationReceipts_OperationId",
                table: "IntegrationReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations");

            migrationBuilder.AddColumn<int>(
                name: "EvidenceNumber",
                table: "IntegrationReceipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_EmailSendPlans_WorkspaceId_Id",
                table: "EmailSendPlans",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateTable(
                name: "EmailSendDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailSendPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationActionRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationOperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProviderCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExternalMessageId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EmailMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    DiagnosticCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSendDeliveries", x => x.Id);
                    table.CheckConstraint("CK_EmailSendDeliveries_Dates", "(\"StartedAt\" IS NULL OR \"StartedAt\" >= \"CreatedAt\") AND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"CreatedAt\")");
                    table.CheckConstraint("CK_EmailSendDeliveries_Identity", "\"OperationType\" = 'email.send' AND \"ContentHash\" ~ '^[0-9a-f]{64}$' AND \"IdempotencyKey\" = 'email-send-' || \"Id\"::text");
                    table.CheckConstraint("CK_EmailSendDeliveries_Proof", "(\"StatusCode\" = 'sent') = (\"EmailMessageId\" IS NOT NULL AND \"ExternalMessageId\" IS NOT NULL) AND (\"StatusCode\" IN ('sent','failed')) = (\"FinishedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_EmailSendDeliveries_Status", "\"StatusCode\" IN ('pending','dispatching','awaiting-result','send-unknown','result-received','sent','failed')");
                    table.ForeignKey(
                        name: "FK_EmailSendDeliveries_AutomationActionRequests_WorkspaceId_Au~",
                        columns: x => new { x.WorkspaceId, x.AutomationActionRequestId },
                        principalTable: "AutomationActionRequests",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmailSendDeliveries_EmailAccounts_WorkspaceId_EmailAccountI~",
                        columns: x => new { x.WorkspaceId, x.EmailAccountId, x.ConnectionId },
                        principalTable: "EmailAccounts",
                        principalColumns: new[] { "WorkspaceId", "Id", "ConnectionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmailSendDeliveries_EmailMessages_EmailMessageId",
                        column: x => x.EmailMessageId,
                        principalTable: "EmailMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmailSendDeliveries_EmailSendPlans_WorkspaceId_EmailSendPla~",
                        columns: x => new { x.WorkspaceId, x.EmailSendPlanId },
                        principalTable: "EmailSendPlans",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmailSendDeliveries_IntegrationOperations_IntegrationOperat~",
                        column: x => x.IntegrationOperationId,
                        principalTable: "IntegrationOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationReceipts_OperationId_EvidenceNumber",
                table: "IntegrationReceipts",
                columns: new[] { "OperationId", "EvidenceNumber" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations",
                sql: "\"Version\" = 1 AND ((\"Type\" = 'technical.probe' AND \"ProbeId\" <> '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"Type\" IN ('collection.request','email.sync','email.send') AND \"ConnectionId\" IS NOT NULL AND \"ProbeId\" = '00000000-0000-0000-0000-000000000000'::uuid))");

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendDeliveries_AutomationActionRequestId",
                table: "EmailSendDeliveries",
                column: "AutomationActionRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendDeliveries_EmailMessageId",
                table: "EmailSendDeliveries",
                column: "EmailMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendDeliveries_EmailSendPlanId",
                table: "EmailSendDeliveries",
                column: "EmailSendPlanId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendDeliveries_IdempotencyKey",
                table: "EmailSendDeliveries",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendDeliveries_IntegrationOperationId",
                table: "EmailSendDeliveries",
                column: "IntegrationOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendDeliveries_WorkspaceId_AutomationActionRequestId",
                table: "EmailSendDeliveries",
                columns: new[] { "WorkspaceId", "AutomationActionRequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendDeliveries_WorkspaceId_EmailAccountId_ConnectionId",
                table: "EmailSendDeliveries",
                columns: new[] { "WorkspaceId", "EmailAccountId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendDeliveries_WorkspaceId_EmailSendPlanId",
                table: "EmailSendDeliveries",
                columns: new[] { "WorkspaceId", "EmailSendPlanId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendDeliveries_WorkspaceId_StatusCode_CreatedAt",
                table: "EmailSendDeliveries",
                columns: new[] { "WorkspaceId", "StatusCode", "CreatedAt" });
            migrationBuilder.Sql("""
                ALTER TABLE "EmailSendDeliveries" ADD CONSTRAINT "FK_EmailDelivery_OperationBinding"
                FOREIGN KEY ("WorkspaceId","ConnectionId","IntegrationOperationId","OperationType")
                REFERENCES "IntegrationOperations" ("WorkspaceId","ConnectionId","Id","Type") ON DELETE RESTRICT;
                CREATE FUNCTION email_delivery_identity_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF (NEW."Id", NEW."WorkspaceId", NEW."EmailSendPlanId", NEW."AutomationActionRequestId", NEW."EmailAccountId",
                        NEW."ConnectionId", NEW."IntegrationOperationId", NEW."ContentHash", NEW."IdempotencyKey", NEW."ProviderCode", NEW."CreatedAt")
                    IS DISTINCT FROM (OLD."Id", OLD."WorkspaceId", OLD."EmailSendPlanId", OLD."AutomationActionRequestId", OLD."EmailAccountId",
                        OLD."ConnectionId", OLD."IntegrationOperationId", OLD."ContentHash", OLD."IdempotencyKey", OLD."ProviderCode", OLD."CreatedAt")
                    OR (OLD."StatusCode" <> 'pending' AND NEW."StatusCode" = 'pending')
                    OR (OLD."StatusCode" IN ('sent','failed') AND NEW IS DISTINCT FROM OLD) THEN
                        RAISE EXCEPTION 'Email delivery identity or terminal state is immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER "TR_EmailDelivery_Identity" BEFORE UPDATE ON "EmailSendDeliveries"
                    FOR EACH ROW EXECUTE FUNCTION email_delivery_identity_guard();
                CREATE FUNCTION email_receipt_slot_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE "Id" = NEW."OperationId")
                    AND NOT EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE "Id" = NEW."OperationId"
                        AND (("Type" = 'email.send' AND NEW."EvidenceNumber" BETWEEN 1 AND 100)
                            OR ("Type" <> 'email.send' AND NEW."EvidenceNumber" = 0))) THEN
                        RAISE EXCEPTION 'Invalid integration evidence slot' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER "TR_IntegrationReceipts_Slot" BEFORE INSERT OR UPDATE ON "IntegrationReceipts"
                    FOR EACH ROW EXECUTE FUNCTION email_receipt_slot_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "EmailSendDeliveries") OR EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE "Type" = 'email.send') THEN
                        RAISE EXCEPTION 'Phase9541 downgrade requires an empty send history';
                    END IF;
                END $$;
                DROP TRIGGER "TR_EmailDelivery_Identity" ON "EmailSendDeliveries";
                DROP FUNCTION email_delivery_identity_guard();
                DROP TRIGGER "TR_IntegrationReceipts_Slot" ON "IntegrationReceipts";
                DROP FUNCTION email_receipt_slot_guard();
                """);
            migrationBuilder.DropTable(
                name: "EmailSendDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_IntegrationReceipts_OperationId_EvidenceNumber",
                table: "IntegrationReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_EmailSendPlans_WorkspaceId_Id",
                table: "EmailSendPlans");

            migrationBuilder.DropColumn(
                name: "EvidenceNumber",
                table: "IntegrationReceipts");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationReceipts_OperationId",
                table: "IntegrationReceipts",
                column: "OperationId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations",
                sql: "\"Version\" = 1 AND ((\"Type\" = 'technical.probe' AND \"ProbeId\" <> '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"Type\" IN ('collection.request','email.sync') AND \"ConnectionId\" IS NOT NULL AND \"ProbeId\" = '00000000-0000-0000-0000-000000000000'::uuid))");
        }
    }
}
