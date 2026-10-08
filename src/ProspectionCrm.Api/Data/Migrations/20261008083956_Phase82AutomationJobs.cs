using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase82AutomationJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_AutomationRules_WorkspaceId_Id",
                table: "AutomationRules",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateTable(
                name: "AutomationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationRuleId = table.Column<Guid>(type: "uuid", nullable: true),
                    TriggerTypeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TriggerKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ActionCategoryCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    Priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 50),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    LeaseOwner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ContextJson = table.Column<string>(type: "jsonb", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationJobs", x => x.Id);
                    table.CheckConstraint("CK_AutomationJobs_Attempts", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_AutomationJobs_Category", "\"ActionCategoryCode\" IN ('general', 'email', 'application')");
                    table.CheckConstraint("CK_AutomationJobs_CompletedAt", "(\"StatusCode\" IN ('completed', 'failed', 'cancelled') AND \"CompletedAt\" IS NOT NULL)\nOR (\"StatusCode\" IN ('pending', 'leased') AND \"CompletedAt\" IS NULL)");
                    table.CheckConstraint("CK_AutomationJobs_Context", "\"ContextJson\" IS NULL OR (jsonb_typeof(\"ContextJson\") = 'object' AND octet_length(\"ContextJson\"::text) <= 65536)");
                    table.CheckConstraint("CK_AutomationJobs_Error", "\"LastError\" IS NULL OR (\"StatusCode\" = 'failed' AND \"LastError\" IN ('AutomationJobFailed', 'AutomationProcessingRejected'))");
                    table.CheckConstraint("CK_AutomationJobs_Lease", "(\"StatusCode\" = 'leased' AND \"LeaseOwner\" IS NOT NULL\n    AND length(btrim(\"LeaseOwner\")) > 0 AND \"LeaseExpiresAt\" IS NOT NULL)\nOR (\"StatusCode\" <> 'leased' AND \"LeaseOwner\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");
                    table.CheckConstraint("CK_AutomationJobs_Priority", "\"Priority\" BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_AutomationJobs_Status", "\"StatusCode\" IN ('pending', 'leased', 'completed', 'failed', 'cancelled')");
                    table.CheckConstraint("CK_AutomationJobs_Trigger", "\"TriggerTypeCode\" IN ('manual')");
                    table.ForeignKey(
                        name: "FK_AutomationJobs_AutomationRules_WorkspaceId_AutomationRuleId",
                        columns: x => new { x.WorkspaceId, x.AutomationRuleId },
                        principalTable: "AutomationRules",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AutomationJobs_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationJobs_Claim",
                table: "AutomationJobs",
                columns: new[] { "WorkspaceId", "Priority", "AvailableAt", "CreatedAt", "Id" },
                descending: new[] { false, true, false, false, false },
                filter: "\"StatusCode\" = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationJobs_Recovery",
                table: "AutomationJobs",
                columns: new[] { "WorkspaceId", "LeaseExpiresAt" },
                filter: "\"StatusCode\" = 'leased'");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationJobs_WorkspaceId_AutomationRuleId",
                table: "AutomationJobs",
                columns: new[] { "WorkspaceId", "AutomationRuleId" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationJobs_WorkspaceId_CreatedAt_Id",
                table: "AutomationJobs",
                columns: new[] { "WorkspaceId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "UX_AutomationJobs_Workspace_Trigger_Key",
                table: "AutomationJobs",
                columns: new[] { "WorkspaceId", "TriggerTypeCode", "TriggerKey" },
                unique: true,
                filter: "\"TriggerKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationJobs");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AutomationRules_WorkspaceId_Id",
                table: "AutomationRules");
        }
    }
}
