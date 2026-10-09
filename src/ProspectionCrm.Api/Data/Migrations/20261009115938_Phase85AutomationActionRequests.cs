using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase85AutomationActionRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationJobs_CompletedAt",
                table: "AutomationJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationJobs_Status",
                table: "AutomationJobs");

            migrationBuilder.DropIndex(
                name: "IX_AutomationExecutions_Quotas",
                table: "AutomationExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AutomationExecutions_WorkspaceId_AutomationJobId",
                table: "AutomationExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationExecutions_Effect",
                table: "AutomationExecutions");

            migrationBuilder.AddColumn<Guid>(
                name: "AutomationActionRequestId",
                table: "AutomationExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHumanApprovedAttempt",
                table: "AutomationExecutions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AutomationActionRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionRequirementCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    ActionTypeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ActionCategoryCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ActionPlanJson = table.Column<string>(type: "jsonb", nullable: false),
                    RuleFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedReasonCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationActionRequests", x => x.Id);
                    table.UniqueConstraint("AK_AutomationActionRequests_WorkspaceId_AutomationJobId_Automa~", x => new { x.WorkspaceId, x.AutomationJobId, x.AutomationRuleId, x.Id });
                    table.CheckConstraint("CK_ActionRequests_Dates", "\"DecidedAt\" IS NULL OR \"DecidedAt\" >= \"RequestedAt\"");
                    table.CheckConstraint("CK_ActionRequests_Decision", "(\"StatusCode\" = 'pending' AND \"DecidedAt\" IS NULL AND \"DecidedByUserId\" IS NULL AND \"DecisionNote\" IS NULL)\nOR (\"StatusCode\" IN ('approved','rejected') AND \"DecidedAt\" IS NOT NULL AND \"DecidedByUserId\" IS NOT NULL)\nOR (\"StatusCode\" = 'cancelled' AND \"DecidedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_ActionRequests_Fingerprint", "\"RuleFingerprint\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_ActionRequests_Plan", "jsonb_typeof(\"ActionPlanJson\") = 'object' AND octet_length(\"ActionPlanJson\"::text) <= 65536");
                    table.CheckConstraint("CK_ActionRequests_Reason", "length(btrim(\"RequestedReasonCode\")) > 0");
                    table.CheckConstraint("CK_ActionRequests_Requirement", "\"DecisionRequirementCode\" IN ('manual','approval-required')");
                    table.CheckConstraint("CK_ActionRequests_Status", "\"StatusCode\" IN ('pending','approved','rejected','cancelled')");
                    table.ForeignKey(
                        name: "FK_AutomationActionRequests_AutomationJobs_WorkspaceId_Automat~",
                        columns: x => new { x.WorkspaceId, x.AutomationJobId },
                        principalTable: "AutomationJobs",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AutomationActionRequests_AutomationRules_WorkspaceId_Automa~",
                        columns: x => new { x.WorkspaceId, x.AutomationRuleId },
                        principalTable: "AutomationRules",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AutomationActionRequests_UserAccounts_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AutomationActionRequests_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationJobs_CompletedAt",
                table: "AutomationJobs",
                sql: "(\"StatusCode\" IN ('completed', 'failed', 'cancelled') AND \"CompletedAt\" IS NOT NULL)\nOR (\"StatusCode\" IN ('pending', 'leased', 'awaiting-approval') AND \"CompletedAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationJobs_Status",
                table: "AutomationJobs",
                sql: "\"StatusCode\" IN ('pending', 'leased', 'completed', 'failed', 'cancelled', 'awaiting-approval')");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_Quotas",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "FinishedAt" },
                filter: "\"EffectApplied\" AND \"IsAutomaticAttempt\"");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_WorkspaceId_AutomationJobId_Automation~",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "AutomationJobId", "AutomationRuleId", "AutomationActionRequestId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationExecutions_Effect",
                table: "AutomationExecutions",
                sql: "NOT \"EffectApplied\" OR (\"StatusCode\" = 'succeeded' AND\n    ((\"IsAutomaticAttempt\" AND \"OutcomeSequence\" IS NOT NULL)\n    OR (\"IsHumanApprovedAttempt\" AND \"OutcomeSequence\" IS NULL)))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationExecutions_Human",
                table: "AutomationExecutions",
                sql: "(NOT \"IsHumanApprovedAttempt\" AND \"AutomationActionRequestId\" IS NULL)\nOR (\"IsHumanApprovedAttempt\" AND NOT \"IsAutomaticAttempt\" AND \"AutomationActionRequestId\" IS NOT NULL\n    AND \"AutomationJobId\" IS NOT NULL AND \"OutcomeSequence\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationActionRequests_DecidedByUserId",
                table: "AutomationActionRequests",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationActionRequests_WorkspaceId_AutomationRuleId",
                table: "AutomationActionRequests",
                columns: new[] { "WorkspaceId", "AutomationRuleId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationActionRequests_WorkspaceId_RequestedAt_Id",
                table: "AutomationActionRequests",
                columns: new[] { "WorkspaceId", "RequestedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationActionRequests_WorkspaceId_StatusCode_RequestedAt~",
                table: "AutomationActionRequests",
                columns: new[] { "WorkspaceId", "StatusCode", "RequestedAt", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "UX_AutomationActionRequests_Job",
                table: "AutomationActionRequests",
                column: "AutomationJobId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AutomationExecutions_AutomationActionRequests_WorkspaceId_A~",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "AutomationJobId", "AutomationRuleId", "AutomationActionRequestId" },
                principalTable: "AutomationActionRequests",
                principalColumns: new[] { "WorkspaceId", "AutomationJobId", "AutomationRuleId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Stop workers before downgrade. Keep all jobs, histories and tasks. 8.4 cannot
            // represent a human effect without falsely counting it as automatic: retain the
            // succeeded checkpoint, but explicitly discard this unsupported effect metadata.
            migrationBuilder.Sql("""
                UPDATE "AutomationJobs" SET "StatusCode" = 'pending', "LeaseOwner" = NULL,
                    "LeaseExpiresAt" = NULL, "CompletedAt" = NULL, "LastError" = NULL
                WHERE "StatusCode" = 'awaiting-approval';
                UPDATE "AutomationExecutions" SET "EffectApplied" = false WHERE "IsHumanApprovedAttempt";
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_AutomationExecutions_AutomationActionRequests_WorkspaceId_A~",
                table: "AutomationExecutions");

            migrationBuilder.DropTable(
                name: "AutomationActionRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationJobs_CompletedAt",
                table: "AutomationJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationJobs_Status",
                table: "AutomationJobs");

            migrationBuilder.DropIndex(
                name: "IX_AutomationExecutions_Quotas",
                table: "AutomationExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AutomationExecutions_WorkspaceId_AutomationJobId_Automation~",
                table: "AutomationExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationExecutions_Effect",
                table: "AutomationExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationExecutions_Human",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "AutomationActionRequestId",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "IsHumanApprovedAttempt",
                table: "AutomationExecutions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationJobs_CompletedAt",
                table: "AutomationJobs",
                sql: "(\"StatusCode\" IN ('completed', 'failed', 'cancelled') AND \"CompletedAt\" IS NOT NULL)\nOR (\"StatusCode\" IN ('pending', 'leased') AND \"CompletedAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationJobs_Status",
                table: "AutomationJobs",
                sql: "\"StatusCode\" IN ('pending', 'leased', 'completed', 'failed', 'cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_Quotas",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "FinishedAt" },
                filter: "\"EffectApplied\"");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_WorkspaceId_AutomationJobId",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "AutomationJobId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationExecutions_Effect",
                table: "AutomationExecutions",
                sql: "NOT \"EffectApplied\" OR (\"StatusCode\" = 'succeeded' AND \"IsAutomaticAttempt\" AND \"OutcomeSequence\" IS NOT NULL)");
        }
    }
}
