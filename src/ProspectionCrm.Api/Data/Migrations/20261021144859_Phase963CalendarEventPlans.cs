using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase963CalendarEventPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ActionRequests_Email",
                table: "AutomationActionRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ActionRequests_Origin",
                table: "AutomationActionRequests");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Calendars_WorkspaceId_Id_CalendarAccountId_ConnectionId",
                table: "Calendars",
                columns: new[] { "WorkspaceId", "Id", "CalendarAccountId", "ConnectionId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CalendarEvents_WorkspaceId_Id",
                table: "CalendarEvents",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateTable(
                name: "CalendarEventPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CalendarAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: true),
                    StatusCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CalendarId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    TargetCalendarEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetExternalEventId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TargetExternalRevision = table.Column<long>(type: "bigint", nullable: true),
                    TargetSnapshotJson = table.Column<string>(type: "jsonb", nullable: true),
                    ContentJson = table.Column<string>(type: "jsonb", nullable: false),
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
                    table.PrimaryKey("PK_CalendarEventPlans", x => x.Id);
                    table.CheckConstraint("CK_CalendarEventPlans_Content", "jsonb_typeof(\"ContentJson\") = 'object' AND octet_length(\"ContentJson\"::text) <= 60000\r\nAND ((\"OperationCode\" = 'create' AND \"TargetCalendarEventId\" IS NULL AND \"TargetExternalEventId\" IS NULL\r\n    AND \"TargetExternalRevision\" IS NULL AND \"TargetSnapshotJson\" IS NULL)\r\nOR (\"OperationCode\" = 'update' AND \"TargetCalendarEventId\" IS NOT NULL AND \"TargetExternalEventId\" IS NOT NULL\r\n    AND \"TargetExternalRevision\" IS NOT NULL AND \"TargetExternalRevision\" >= 0\r\n    AND \"TargetSnapshotJson\" IS NOT NULL AND jsonb_typeof(\"TargetSnapshotJson\") = 'object'))");
                    table.CheckConstraint("CK_CalendarEventPlans_Dates", "(\"UpdatedAt\" IS NULL OR \"UpdatedAt\" >= \"CreatedAt\") AND (\"SubmittedAt\" IS NULL OR \"SubmittedAt\" >= \"CreatedAt\")\r\nAND (\"ApprovedAt\" IS NULL OR \"ApprovedAt\" >= \"SubmittedAt\") AND (\"RejectedAt\" IS NULL OR \"RejectedAt\" >= \"SubmittedAt\")\r\nAND (\"CancelledAt\" IS NULL OR \"CancelledAt\" >= COALESCE(\"SubmittedAt\", \"CreatedAt\"))");
                    table.CheckConstraint("CK_CalendarEventPlans_Hash", "\"ContentHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_CalendarEventPlans_Revision", "\"Revision\" >= 0 AND (\"SubmittedRevision\" IS NULL OR (\"SubmittedRevision\" >= 0 AND \"SubmittedRevision\" < \"Revision\"))");
                    table.CheckConstraint("CK_CalendarEventPlans_State", "(\"StatusCode\" = 'draft' AND \"AutomationActionRequestId\" IS NULL AND \"SubmittedAt\" IS NULL AND \"SubmittedRevision\" IS NULL\r\n    AND \"ApprovedAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL)\r\nOR (\"StatusCode\" IN ('pending-approval','approved','rejected') AND \"AutomationActionRequestId\" IS NOT NULL\r\n    AND \"SubmittedAt\" IS NOT NULL AND \"SubmittedRevision\" IS NOT NULL AND \"CancelledAt\" IS NULL\r\n    AND ((\"StatusCode\" = 'pending-approval' AND \"ApprovedAt\" IS NULL AND \"RejectedAt\" IS NULL)\r\n        OR (\"StatusCode\" = 'approved' AND \"ApprovedAt\" IS NOT NULL AND \"RejectedAt\" IS NULL)\r\n        OR (\"StatusCode\" = 'rejected' AND \"RejectedAt\" IS NOT NULL AND \"ApprovedAt\" IS NULL)))\r\nOR (\"StatusCode\" = 'cancelled' AND \"CancelledAt\" IS NOT NULL AND \"ApprovedAt\" IS NULL AND \"RejectedAt\" IS NULL\r\n    AND ((\"AutomationActionRequestId\" IS NULL AND \"SubmittedRevision\" IS NULL AND \"SubmittedAt\" IS NULL)\r\n        OR (\"AutomationActionRequestId\" IS NOT NULL AND \"SubmittedRevision\" IS NOT NULL AND \"SubmittedAt\" IS NOT NULL)))");
                    table.ForeignKey(
                        name: "FK_CalendarEventPlans_AutomationActionRequests_WorkspaceId_Aut~",
                        columns: x => new { x.WorkspaceId, x.AutomationActionRequestId },
                        principalTable: "AutomationActionRequests",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEventPlans_CalendarAccounts_WorkspaceId_CalendarAcc~",
                        columns: x => new { x.WorkspaceId, x.CalendarAccountId, x.ConnectionId },
                        principalTable: "CalendarAccounts",
                        principalColumns: new[] { "WorkspaceId", "Id", "ConnectionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEventPlans_CalendarEvents_WorkspaceId_TargetCalenda~",
                        columns: x => new { x.WorkspaceId, x.TargetCalendarEventId },
                        principalTable: "CalendarEvents",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEventPlans_Calendars_WorkspaceId_CalendarId_Calenda~",
                        columns: x => new { x.WorkspaceId, x.CalendarId, x.CalendarAccountId, x.ConnectionId },
                        principalTable: "Calendars",
                        principalColumns: new[] { "WorkspaceId", "Id", "CalendarAccountId", "ConnectionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEventPlans_Companies_WorkspaceId_CompanyId",
                        columns: x => new { x.WorkspaceId, x.CompanyId },
                        principalTable: "Companies",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEventPlans_Contacts_WorkspaceId_ContactId",
                        columns: x => new { x.WorkspaceId, x.ContactId },
                        principalTable: "Contacts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEventPlans_Opportunities_WorkspaceId_OpportunityId",
                        columns: x => new { x.WorkspaceId, x.OpportunityId },
                        principalTable: "Opportunities",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarEventPlans_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActionRequests_Email",
                table: "AutomationActionRequests",
                sql: "(\"ActionTypeCode\" NOT IN ('email.send','calendar.event.create','calendar.event.update') AND \"ContentFingerprint\" IS NULL)\r\nOR (\"ActionTypeCode\" = 'email.send' AND \"ActionCategoryCode\" = 'email'\r\n    AND \"ContentFingerprint\" IS NOT NULL AND \"ContentFingerprint\" ~ '^[0-9a-f]{64}$')\r\nOR (\"ActionTypeCode\" IN ('calendar.event.create','calendar.event.update') AND \"ActionCategoryCode\" = 'calendar'\r\n    AND \"ContentFingerprint\" IS NOT NULL AND \"ContentFingerprint\" ~ '^[0-9a-f]{64}$')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActionRequests_Origin",
                table: "AutomationActionRequests",
                sql: "(\"AutomationJobId\" IS NOT NULL AND \"AutomationRuleId\" IS NOT NULL AND \"ActionTypeCode\" NOT IN ('calendar.event.create','calendar.event.update'))\r\nOR (\"AutomationJobId\" IS NULL AND \"AutomationRuleId\" IS NULL AND \"ActionTypeCode\" IN ('email.send','calendar.event.create','calendar.event.update'))");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPlans_AutomationActionRequestId",
                table: "CalendarEventPlans",
                column: "AutomationActionRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPlans_WorkspaceId_AutomationActionRequestId",
                table: "CalendarEventPlans",
                columns: new[] { "WorkspaceId", "AutomationActionRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPlans_WorkspaceId_CalendarAccountId_Connection~",
                table: "CalendarEventPlans",
                columns: new[] { "WorkspaceId", "CalendarAccountId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPlans_WorkspaceId_CalendarId_CalendarAccountId~",
                table: "CalendarEventPlans",
                columns: new[] { "WorkspaceId", "CalendarId", "CalendarAccountId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPlans_WorkspaceId_CompanyId",
                table: "CalendarEventPlans",
                columns: new[] { "WorkspaceId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPlans_WorkspaceId_ContactId",
                table: "CalendarEventPlans",
                columns: new[] { "WorkspaceId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPlans_WorkspaceId_CreatedAt_Id",
                table: "CalendarEventPlans",
                columns: new[] { "WorkspaceId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPlans_WorkspaceId_OpportunityId",
                table: "CalendarEventPlans",
                columns: new[] { "WorkspaceId", "OpportunityId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventPlans_WorkspaceId_TargetCalendarEventId",
                table: "CalendarEventPlans",
                columns: new[] { "WorkspaceId", "TargetCalendarEventId" });
            migrationBuilder.Sql("""
                CREATE FUNCTION "Phase963FreezeCalendarPlan"() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Calendar plan history must be retained' USING ERRCODE = '23514'; END IF;
                    IF (OLD."StatusCode" IN ('approved','rejected','cancelled') AND NEW IS DISTINCT FROM OLD)
                        OR (OLD."StatusCode" = 'pending-approval' AND NEW."StatusCode" NOT IN ('pending-approval','approved','rejected','cancelled'))
                        OR (OLD."StatusCode" = 'draft' AND NEW."StatusCode" NOT IN ('draft','pending-approval','cancelled')) THEN
                        RAISE EXCEPTION 'Invalid calendar plan transition' USING ERRCODE = '23514';
                    END IF;
                    IF OLD."SubmittedRevision" IS NOT NULL AND
                        (to_jsonb(NEW) - ARRAY['StatusCode','Revision','UpdatedAt','ApprovedAt','RejectedAt','CancelledAt']) IS DISTINCT FROM
                        (to_jsonb(OLD) - ARRAY['StatusCode','Revision','UpdatedAt','ApprovedAt','RejectedAt','CancelledAt']) THEN
                        RAISE EXCEPTION 'Calendar snapshot is immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER "TR_CalendarEventPlans_Immutable" BEFORE UPDATE OR DELETE ON "CalendarEventPlans"
                    FOR EACH ROW EXECUTE FUNCTION "Phase963FreezeCalendarPlan"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "CalendarEventPlans") OR EXISTS
                        (SELECT 1 FROM "AutomationActionRequests" WHERE "ActionTypeCode" IN ('calendar.event.create','calendar.event.update')) THEN
                        RAISE EXCEPTION 'Phase963 downgrade requires an empty calendar preparation history';
                    END IF;
                END $$;
                DROP TRIGGER "TR_CalendarEventPlans_Immutable" ON "CalendarEventPlans";
                DROP FUNCTION "Phase963FreezeCalendarPlan"();
                """);

            migrationBuilder.DropTable(
                name: "CalendarEventPlans");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Calendars_WorkspaceId_Id_CalendarAccountId_ConnectionId",
                table: "Calendars");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CalendarEvents_WorkspaceId_Id",
                table: "CalendarEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ActionRequests_Email",
                table: "AutomationActionRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ActionRequests_Origin",
                table: "AutomationActionRequests");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActionRequests_Email",
                table: "AutomationActionRequests",
                sql: "(\"ActionTypeCode\" <> 'email.send' AND \"ContentFingerprint\" IS NULL)\nOR (\"ActionTypeCode\" = 'email.send' AND \"ActionCategoryCode\" = 'email'\n    AND \"ContentFingerprint\" IS NOT NULL AND \"ContentFingerprint\" ~ '^[0-9a-f]{64}$')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActionRequests_Origin",
                table: "AutomationActionRequests",
                sql: "(\"AutomationJobId\" IS NOT NULL AND \"AutomationRuleId\" IS NOT NULL)\nOR (\"AutomationJobId\" IS NULL AND \"AutomationRuleId\" IS NULL AND \"ActionTypeCode\" = 'email.send')");
        }
    }
}
