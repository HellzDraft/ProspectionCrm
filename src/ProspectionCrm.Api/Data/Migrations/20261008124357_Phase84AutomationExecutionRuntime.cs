using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase84AutomationExecutionRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AutomationJobId",
                table: "CrmTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActionTypeCode",
                table: "AutomationExecutions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttemptNumber",
                table: "AutomationExecutions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AutomationJobId",
                table: "AutomationExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EffectApplied",
                table: "AutomationExecutions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsAutomaticAttempt",
                table: "AutomationExecutions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeferred",
                table: "AutomationExecutions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "OutcomeSequence",
                table: "AutomationExecutions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReasonCode",
                table: "AutomationExecutions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_AutomationJobs_WorkspaceId_Id",
                table: "AutomationJobs",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_CrmTasks_AutomationJob",
                table: "CrmTasks",
                column: "AutomationJobId",
                unique: true,
                filter: "\"AutomationJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_Quotas",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "FinishedAt" },
                filter: "\"EffectApplied\"");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationExecutions_WorkspaceId_AutomationJobId",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "AutomationJobId" });

            migrationBuilder.CreateIndex(
                name: "UX_AutomationExecutions_Job_Attempt",
                table: "AutomationExecutions",
                columns: new[] { "AutomationJobId", "AttemptNumber" },
                unique: true,
                filter: "\"AutomationJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_AutomationExecutions_Job_Effect",
                table: "AutomationExecutions",
                column: "AutomationJobId",
                unique: true,
                filter: "\"EffectApplied\"");

            migrationBuilder.CreateIndex(
                name: "UX_AutomationExecutions_Workspace_Outcome",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "OutcomeSequence" },
                unique: true,
                filter: "\"OutcomeSequence\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationExecutions_Deferred",
                table: "AutomationExecutions",
                sql: "NOT \"IsDeferred\" OR (\"StatusCode\" = 'skipped' AND NOT \"EffectApplied\" AND \"OutcomeSequence\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationExecutions_Effect",
                table: "AutomationExecutions",
                sql: "NOT \"EffectApplied\" OR (\"StatusCode\" = 'succeeded' AND \"IsAutomaticAttempt\" AND \"OutcomeSequence\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationExecutions_Outcome",
                table: "AutomationExecutions",
                sql: "\"OutcomeSequence\" IS NULL OR (\"OutcomeSequence\" > 0 AND \"IsAutomaticAttempt\"\n    AND (\"EffectApplied\" OR \"StatusCode\" = 'failed'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationExecutions_Runtime",
                table: "AutomationExecutions",
                sql: "(\"AutomationJobId\" IS NULL AND \"AttemptNumber\" IS NULL AND NOT \"IsAutomaticAttempt\"\n    AND NOT \"IsDeferred\" AND NOT \"EffectApplied\" AND \"OutcomeSequence\" IS NULL)\nOR (\"AutomationJobId\" IS NOT NULL AND \"AttemptNumber\" IS NOT NULL AND \"AttemptNumber\" > 0\n    AND \"StartedAt\" IS NOT NULL AND\n    ((\"StatusCode\" = 'running' AND \"FinishedAt\" IS NULL)\n    OR (\"StatusCode\" IN ('succeeded','failed','skipped','cancelled') AND \"FinishedAt\" IS NOT NULL)))");

            migrationBuilder.AddForeignKey(
                name: "FK_AutomationExecutions_AutomationJobs_WorkspaceId_AutomationJ~",
                table: "AutomationExecutions",
                columns: new[] { "WorkspaceId", "AutomationJobId" },
                principalTable: "AutomationJobs",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CrmTasks_AutomationJobs_AutomationJobId",
                table: "CrmTasks",
                column: "AutomationJobId",
                principalTable: "AutomationJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AutomationExecutions_AutomationJobs_WorkspaceId_AutomationJ~",
                table: "AutomationExecutions");

            migrationBuilder.DropForeignKey(
                name: "FK_CrmTasks_AutomationJobs_AutomationJobId",
                table: "CrmTasks");

            migrationBuilder.DropIndex(
                name: "UX_CrmTasks_AutomationJob",
                table: "CrmTasks");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AutomationJobs_WorkspaceId_Id",
                table: "AutomationJobs");

            migrationBuilder.DropIndex(
                name: "IX_AutomationExecutions_Quotas",
                table: "AutomationExecutions");

            migrationBuilder.DropIndex(
                name: "IX_AutomationExecutions_WorkspaceId_AutomationJobId",
                table: "AutomationExecutions");

            migrationBuilder.DropIndex(
                name: "UX_AutomationExecutions_Job_Attempt",
                table: "AutomationExecutions");

            migrationBuilder.DropIndex(
                name: "UX_AutomationExecutions_Job_Effect",
                table: "AutomationExecutions");

            migrationBuilder.DropIndex(
                name: "UX_AutomationExecutions_Workspace_Outcome",
                table: "AutomationExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationExecutions_Deferred",
                table: "AutomationExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationExecutions_Effect",
                table: "AutomationExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationExecutions_Outcome",
                table: "AutomationExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationExecutions_Runtime",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "AutomationJobId",
                table: "CrmTasks");

            migrationBuilder.DropColumn(
                name: "ActionTypeCode",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "AttemptNumber",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "AutomationJobId",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "EffectApplied",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "IsAutomaticAttempt",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "IsDeferred",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "OutcomeSequence",
                table: "AutomationExecutions");

            migrationBuilder.DropColumn(
                name: "ReasonCode",
                table: "AutomationExecutions");
        }
    }
}
