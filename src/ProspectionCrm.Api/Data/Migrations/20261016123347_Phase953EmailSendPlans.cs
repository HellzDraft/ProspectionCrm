using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase953EmailSendPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AutomationExecutions_AutomationActionRequests_WorkspaceId_A~",
                table: "AutomationExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AutomationExecutions_WorkspaceId_AutomationJobId_Automation~",
                table: "AutomationExecutions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AutomationActionRequests_WorkspaceId_AutomationJobId_Automa~",
                table: "AutomationActionRequests");

            migrationBuilder.AlterColumn<Guid>(
                name: "AutomationRuleId",
                table: "AutomationActionRequests",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "AutomationJobId",
                table: "AutomationActionRequests",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "ContentFingerprint",
                table: "AutomationActionRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Contacts_WorkspaceId_Id",
                table: "Contacts",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Companies_WorkspaceId_Id",
                table: "Companies",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_AutomationActionRequests_WorkspaceId_Id",
                table: "AutomationActionRequests",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateTable(
                name: "EmailSendPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                    StatusCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    FromAddressSnapshot = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ToAddressesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CcAddressesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Subject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    BodyText = table.Column<string>(type: "character varying(32000)", maxLength: 32000, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    SubmittedRevision = table.Column<long>(type: "bigint", nullable: true),
                    AutomationActionRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSendPlans", x => x.Id);
                    table.CheckConstraint("CK_EmailSendPlans_Dates", "(\"UpdatedAt\" IS NULL OR \"UpdatedAt\" >= \"CreatedAt\") AND (\"SubmittedAt\" IS NULL OR \"SubmittedAt\" >= \"CreatedAt\")\nAND (\"ApprovedAt\" IS NULL OR \"ApprovedAt\" >= \"SubmittedAt\") AND (\"RejectedAt\" IS NULL OR \"RejectedAt\" >= \"SubmittedAt\")\nAND (\"CancelledAt\" IS NULL OR \"CancelledAt\" >= COALESCE(\"SubmittedAt\", \"CreatedAt\"))");
                    table.CheckConstraint("CK_EmailSendPlans_Hash", "\"ContentHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_EmailSendPlans_Recipients", "jsonb_typeof(\"ToAddressesJson\") = 'array' AND jsonb_typeof(\"CcAddressesJson\") = 'array'\nAND jsonb_array_length(\"ToAddressesJson\") > 0\nAND jsonb_array_length(\"ToAddressesJson\") + jsonb_array_length(\"CcAddressesJson\") <= 100");
                    table.CheckConstraint("CK_EmailSendPlans_Revision", "\"Revision\" >= 0 AND (\"SubmittedRevision\" IS NULL OR (\"SubmittedRevision\" >= 0 AND \"SubmittedRevision\" < \"Revision\"))");
                    table.CheckConstraint("CK_EmailSendPlans_State", "(\"StatusCode\" = 'draft' AND \"AutomationActionRequestId\" IS NULL AND \"SubmittedAt\" IS NULL AND \"SubmittedRevision\" IS NULL\n    AND \"ApprovedAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL)\nOR (\"StatusCode\" IN ('pending-approval','approved','rejected') AND \"AutomationActionRequestId\" IS NOT NULL\n    AND \"SubmittedAt\" IS NOT NULL AND \"SubmittedRevision\" IS NOT NULL AND \"CancelledAt\" IS NULL\n    AND ((\"StatusCode\" = 'pending-approval' AND \"ApprovedAt\" IS NULL AND \"RejectedAt\" IS NULL)\n        OR (\"StatusCode\" = 'approved' AND \"ApprovedAt\" IS NOT NULL AND \"RejectedAt\" IS NULL)\n        OR (\"StatusCode\" = 'rejected' AND \"RejectedAt\" IS NOT NULL AND \"ApprovedAt\" IS NULL)))\nOR (\"StatusCode\" = 'cancelled' AND \"CancelledAt\" IS NOT NULL AND \"ApprovedAt\" IS NULL AND \"RejectedAt\" IS NULL\n    AND ((\"AutomationActionRequestId\" IS NULL AND \"SubmittedRevision\" IS NULL AND \"SubmittedAt\" IS NULL)\n        OR (\"AutomationActionRequestId\" IS NOT NULL AND \"SubmittedRevision\" IS NOT NULL AND \"SubmittedAt\" IS NOT NULL)))");
                    table.ForeignKey(
                        name: "FK_EmailSendPlans_AutomationActionRequests_WorkspaceId_Automat~",
                        columns: x => new { x.WorkspaceId, x.AutomationActionRequestId },
                        principalTable: "AutomationActionRequests",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmailSendPlans_Companies_WorkspaceId_CompanyId",
                        columns: x => new { x.WorkspaceId, x.CompanyId },
                        principalTable: "Companies",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmailSendPlans_Contacts_WorkspaceId_ContactId",
                        columns: x => new { x.WorkspaceId, x.ContactId },
                        principalTable: "Contacts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmailSendPlans_EmailAccounts_WorkspaceId_EmailAccountId_Con~",
                        columns: x => new { x.WorkspaceId, x.EmailAccountId, x.ConnectionId },
                        principalTable: "EmailAccounts",
                        principalColumns: new[] { "WorkspaceId", "Id", "ConnectionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmailSendPlans_Opportunities_WorkspaceId_OpportunityId",
                        columns: x => new { x.WorkspaceId, x.OpportunityId },
                        principalTable: "Opportunities",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmailSendPlans_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_WorkspaceId_AutomationActionRequestId",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "AutomationActionRequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_WorkspaceId_AutomationJobId",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "AutomationJobId" });

            migrationBuilder.CreateIndex(
                name: "UX_ActionRequests_ExecutionBinding",
                table: "AutomationActionRequests",
                columns: new[] { "WorkspaceId", "AutomationJobId", "AutomationRuleId", "Id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActionRequests_Email",
                table: "AutomationActionRequests",
                sql: "(\"ActionTypeCode\" <> 'email.send' AND \"ContentFingerprint\" IS NULL)\nOR (\"ActionTypeCode\" = 'email.send' AND \"ActionCategoryCode\" = 'email'\n    AND \"ContentFingerprint\" IS NOT NULL AND \"ContentFingerprint\" ~ '^[0-9a-f]{64}$')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActionRequests_Origin",
                table: "AutomationActionRequests",
                sql: "(\"AutomationJobId\" IS NOT NULL AND \"AutomationRuleId\" IS NOT NULL)\nOR (\"AutomationJobId\" IS NULL AND \"AutomationRuleId\" IS NULL AND \"ActionTypeCode\" = 'email.send')");

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendPlans_AutomationActionRequestId",
                table: "EmailSendPlans",
                column: "AutomationActionRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendPlans_WorkspaceId_AutomationActionRequestId",
                table: "EmailSendPlans",
                columns: new[] { "WorkspaceId", "AutomationActionRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendPlans_WorkspaceId_CompanyId",
                table: "EmailSendPlans",
                columns: new[] { "WorkspaceId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendPlans_WorkspaceId_ContactId",
                table: "EmailSendPlans",
                columns: new[] { "WorkspaceId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendPlans_WorkspaceId_CreatedAt_Id",
                table: "EmailSendPlans",
                columns: new[] { "WorkspaceId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendPlans_WorkspaceId_EmailAccountId_ConnectionId",
                table: "EmailSendPlans",
                columns: new[] { "WorkspaceId", "EmailAccountId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailSendPlans_WorkspaceId_OpportunityId",
                table: "EmailSendPlans",
                columns: new[] { "WorkspaceId", "OpportunityId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AutomationExecutions_AutomationActionRequests_WorkspaceId_A~",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "AutomationActionRequestId" },
                principalTable: "AutomationActionRequests",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);
            AddIntegrityGuards(migrationBuilder);
        }

        private static void AddIntegrityGuards(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
            ALTER TABLE "AutomationExecutions" ADD CONSTRAINT "FK_Executions_RequestOrigin953"
                FOREIGN KEY ("WorkspaceId", "AutomationJobId", "AutomationRuleId", "AutomationActionRequestId")
                REFERENCES "AutomationActionRequests" ("WorkspaceId", "AutomationJobId", "AutomationRuleId", "Id") ON DELETE RESTRICT;
            CREATE FUNCTION "Phase953FreezeEmailPlan"() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                IF OLD."SubmittedRevision" IS NOT NULL AND
                    (NEW."WorkspaceId", NEW."EmailAccountId", NEW."ConnectionId", NEW."FromAddressSnapshot", NEW."ToAddressesJson", NEW."CcAddressesJson",
                     NEW."Subject", NEW."BodyText", NEW."OpportunityId", NEW."CompanyId", NEW."ContactId", NEW."ContentHash",
                     NEW."SubmittedRevision", NEW."AutomationActionRequestId", NEW."SubmittedAt", NEW."CreatedAt") IS DISTINCT FROM
                    (OLD."WorkspaceId", OLD."EmailAccountId", OLD."ConnectionId", OLD."FromAddressSnapshot", OLD."ToAddressesJson", OLD."CcAddressesJson",
                     OLD."Subject", OLD."BodyText", OLD."OpportunityId", OLD."CompanyId", OLD."ContactId", OLD."ContentHash",
                     OLD."SubmittedRevision", OLD."AutomationActionRequestId", OLD."SubmittedAt", OLD."CreatedAt") THEN
                    RAISE EXCEPTION 'Email snapshot is immutable' USING ERRCODE = '23514';
                END IF;
                IF (OLD."StatusCode" IN ('approved','rejected','cancelled') AND NEW IS DISTINCT FROM OLD)
                    OR (OLD."StatusCode" = 'pending-approval' AND NEW."StatusCode" = 'draft') THEN
                    RAISE EXCEPTION 'Email preparation is terminal' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER "TR_EmailSendPlans_Immutable" BEFORE UPDATE ON "EmailSendPlans"
                FOR EACH ROW EXECUTE FUNCTION "Phase953FreezeEmailPlan"();
            """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "EmailSendPlans") OR EXISTS (SELECT 1 FROM "AutomationActionRequests" WHERE "ActionTypeCode" = 'email.send') THEN
                        RAISE EXCEPTION 'Phase953 downgrade requires an empty email preparation history';
                    END IF;
                END $$;
                DROP TRIGGER "TR_EmailSendPlans_Immutable" ON "EmailSendPlans";
                DROP FUNCTION "Phase953FreezeEmailPlan"();
                ALTER TABLE "AutomationExecutions" DROP CONSTRAINT "FK_Executions_RequestOrigin953";
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_AutomationExecutions_AutomationActionRequests_WorkspaceId_A~",
                table: "AutomationExecutions");

            migrationBuilder.DropTable(
                name: "EmailSendPlans");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Contacts_WorkspaceId_Id",
                table: "Contacts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Companies_WorkspaceId_Id",
                table: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_AutomationExecutions_WorkspaceId_AutomationActionRequestId",
                table: "AutomationExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AutomationExecutions_WorkspaceId_AutomationJobId",
                table: "AutomationExecutions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AutomationActionRequests_WorkspaceId_Id",
                table: "AutomationActionRequests");

            migrationBuilder.DropIndex(
                name: "UX_ActionRequests_ExecutionBinding",
                table: "AutomationActionRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ActionRequests_Email",
                table: "AutomationActionRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ActionRequests_Origin",
                table: "AutomationActionRequests");

            migrationBuilder.DropColumn(
                name: "ContentFingerprint",
                table: "AutomationActionRequests");

            migrationBuilder.AlterColumn<Guid>(
                name: "AutomationRuleId",
                table: "AutomationActionRequests",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "AutomationJobId",
                table: "AutomationActionRequests",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_AutomationActionRequests_WorkspaceId_AutomationJobId_Automa~",
                table: "AutomationActionRequests",
                columns: new[] { "WorkspaceId", "AutomationJobId", "AutomationRuleId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_WorkspaceId_AutomationJobId_Automation~",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "AutomationJobId", "AutomationRuleId", "AutomationActionRequestId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AutomationExecutions_AutomationActionRequests_WorkspaceId_A~",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "AutomationJobId", "AutomationRuleId", "AutomationActionRequestId" },
                principalTable: "AutomationActionRequests",
                principalColumns: new[] { "WorkspaceId", "AutomationJobId", "AutomationRuleId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
