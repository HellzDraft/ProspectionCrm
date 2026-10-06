using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase621IngestionHistoryFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContextSnapshotJson",
                table: "SourceExecutions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContractVersion",
                table: "SourceExecutions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HistoryVersion",
                table: "SourceExecutions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ItemsCancelled",
                table: "SourceExecutions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ItemsNotProcessed",
                table: "SourceExecutions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ItemsRejected",
                table: "SourceExecutions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ItemsRolledBack",
                table: "SourceExecutions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "NormalizationVersion",
                table: "SourceExecutions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetPipelineId",
                table: "SourceExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetPipelineStageId",
                table: "SourceExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SourceExecutions_WorkspaceId_Id",
                table: "SourceExecutions",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateTable(
                name: "SourceExecutionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemIndex = table.Column<int>(type: "integer", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    OpportunityIdSnapshot = table.Column<Guid>(type: "uuid", nullable: true),
                    OutcomeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DecisionCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedTitle = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    CompanyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NormalizedCompanyName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    ExternalId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    NormalizedSourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    PayloadSnapshotJson = table.Column<string>(type: "jsonb", nullable: true),
                    DecisionDetailsJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceExecutionItems", x => x.Id);
                    table.UniqueConstraint("AK_SourceExecutionItems_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_SourceExecutionItems_CompanyPair", "(\"CompanyName\" IS NULL) = (\"NormalizedCompanyName\" IS NULL)");
                    table.CheckConstraint("CK_SourceExecutionItems_DecisionDetailsJson", "\"DecisionDetailsJson\" IS NULL OR jsonb_typeof(\"DecisionDetailsJson\") = 'object'");
                    table.CheckConstraint("CK_SourceExecutionItems_ItemIndex", "\"ItemIndex\" >= 0");
                    table.CheckConstraint("CK_SourceExecutionItems_OpportunitySnapshot", "\"OpportunityId\" IS NULL OR (\"OpportunityIdSnapshot\" IS NOT NULL AND \"OpportunityId\" = \"OpportunityIdSnapshot\")");
                    table.CheckConstraint("CK_SourceExecutionItems_OutcomeCode", "\"OutcomeCode\" IN ('pending', 'created', 'updated', 'ignored', 'rejected', 'rolled-back', 'not-processed', 'cancelled')");
                    table.CheckConstraint("CK_SourceExecutionItems_OutcomeOpportunity", "(\"OutcomeCode\" NOT IN ('created', 'updated', 'ignored') OR \"OpportunityIdSnapshot\" IS NOT NULL)\nAND (\"OutcomeCode\" NOT IN ('rejected', 'rolled-back', 'not-processed', 'cancelled')\n     OR (\"OpportunityId\" IS NULL AND \"OpportunityIdSnapshot\" IS NULL))");
                    table.CheckConstraint("CK_SourceExecutionItems_PayloadSnapshotJson", "\"PayloadSnapshotJson\" IS NULL OR jsonb_typeof(\"PayloadSnapshotJson\") = 'object'");
                    table.CheckConstraint("CK_SourceExecutionItems_ProcessedAt", "(\"OutcomeCode\" = 'pending' AND \"ProcessedAt\" IS NULL) OR\n(\"OutcomeCode\" <> 'pending' AND \"ProcessedAt\" IS NOT NULL AND \"ProcessedAt\" >= \"ReceivedAt\")");
                    table.CheckConstraint("CK_SourceExecutionItems_UrlPair", "(\"SourceUrl\" IS NULL) = (\"NormalizedSourceUrl\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_SourceExecutionItems_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SourceExecutionItems_SourceExecutions_WorkspaceId_SourceExe~",
                        columns: x => new { x.WorkspaceId, x.SourceExecutionId },
                        principalTable: "SourceExecutions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceExecutionItemSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceExecutionItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunitySourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    OpportunitySourceIdSnapshot = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceExecutionItemSources", x => x.Id);
                    table.CheckConstraint("CK_SourceExecutionItemSources_RoleCode", "\"RoleCode\" IN ('external-id', 'source-url')");
                    table.CheckConstraint("CK_SourceExecutionItemSources_SourceSnapshot", "\"OpportunitySourceId\" IS NULL OR \"OpportunitySourceId\" = \"OpportunitySourceIdSnapshot\"");
                    table.ForeignKey(
                        name: "FK_SourceExecutionItemSources_OpportunitySources_OpportunitySo~",
                        column: x => x.OpportunitySourceId,
                        principalTable: "OpportunitySources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SourceExecutionItemSources_SourceExecutionItems_WorkspaceId~",
                        columns: x => new { x.WorkspaceId, x.SourceExecutionItemId },
                        principalTable: "SourceExecutionItems",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceExecutions_ContextSnapshotJson",
                table: "SourceExecutions",
                sql: "\"ContextSnapshotJson\" IS NULL OR jsonb_typeof(\"ContextSnapshotJson\") = 'object'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceExecutions_HistoryCounters",
                table: "SourceExecutions",
                sql: "\"ItemsRejected\" >= 0 AND \"ItemsRolledBack\" >= 0 AND \"ItemsNotProcessed\" >= 0 AND \"ItemsCancelled\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceExecutions_HistoryVersions",
                table: "SourceExecutions",
                sql: "(\"HistoryVersion\" IS NULL AND \"ContractVersion\" IS NULL AND \"NormalizationVersion\" IS NULL\n AND \"TargetPipelineId\" IS NULL AND \"TargetPipelineStageId\" IS NULL AND \"ContextSnapshotJson\" IS NULL)\nOR (\"HistoryVersion\" IS NOT NULL AND \"HistoryVersion\" = 1\n    AND \"ContractVersion\" IS NOT NULL AND \"ContractVersion\" = 1\n    AND \"NormalizationVersion\" IS NOT NULL AND \"NormalizationVersion\" = 1\n    AND \"TargetPipelineId\" IS NOT NULL AND \"TargetPipelineStageId\" IS NOT NULL AND \"ContextSnapshotJson\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceExecutions_OutcomeCounters",
                table: "SourceExecutions",
                sql: "\"HistoryVersion\" IS NULL OR (\n    (\"StatusCode\" <> 'succeeded' OR (\"ItemsRejected\" = 0 AND \"ItemsRolledBack\" = 0 AND \"ItemsNotProcessed\" = 0 AND \"ItemsCancelled\" = 0))\n    AND (\"StatusCode\" <> 'failed' OR (\"ItemsCreated\" = 0 AND \"ItemsUpdated\" = 0 AND \"ItemsIgnored\" = 0 AND \"ItemsCancelled\" = 0))\n    AND (\"StatusCode\" <> 'cancelled' OR (\"ItemsCreated\" = 0 AND \"ItemsUpdated\" = 0 AND \"ItemsIgnored\" = 0 AND \"ItemsRejected\" = 0)))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceExecutions_TerminalCounters",
                table: "SourceExecutions",
                sql: "\"HistoryVersion\" IS NULL OR \"StatusCode\" = 'running' OR\n\"ItemsFound\"::bigint = \"ItemsCreated\"::bigint + \"ItemsUpdated\" + \"ItemsIgnored\"\n    + \"ItemsRejected\" + \"ItemsRolledBack\" + \"ItemsNotProcessed\" + \"ItemsCancelled\"");

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutionItems_OpportunityId",
                table: "SourceExecutionItems",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutionItems_SourceExecutionId_ItemIndex",
                table: "SourceExecutionItems",
                columns: new[] { "SourceExecutionId", "ItemIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutionItems_SourceExecutionId_OutcomeCode_ItemIndex",
                table: "SourceExecutionItems",
                columns: new[] { "SourceExecutionId", "OutcomeCode", "ItemIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutionItems_WorkspaceId_NormalizedTitle_Normalized~",
                table: "SourceExecutionItems",
                columns: new[] { "WorkspaceId", "NormalizedTitle", "NormalizedCompanyName" },
                filter: "\"NormalizedCompanyName\" IS NOT NULL AND \"OpportunityIdSnapshot\" IS NOT NULL\nAND \"OutcomeCode\" IN ('created', 'updated', 'ignored')");

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutionItems_WorkspaceId_OpportunityId_ReceivedAt_Id",
                table: "SourceExecutionItems",
                columns: new[] { "WorkspaceId", "OpportunityId", "ReceivedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutionItems_WorkspaceId_SourceExecutionId",
                table: "SourceExecutionItems",
                columns: new[] { "WorkspaceId", "SourceExecutionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutionItemSources_OpportunitySourceId",
                table: "SourceExecutionItemSources",
                column: "OpportunitySourceId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutionItemSources_SourceExecutionItemId_Opportunit~",
                table: "SourceExecutionItemSources",
                columns: new[] { "SourceExecutionItemId", "OpportunitySourceIdSnapshot", "RoleCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutionItemSources_SourceExecutionItemId_RoleCode",
                table: "SourceExecutionItemSources",
                columns: new[] { "SourceExecutionItemId", "RoleCode" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutionItemSources_WorkspaceId_OpportunitySourceId",
                table: "SourceExecutionItemSources",
                columns: new[] { "WorkspaceId", "OpportunitySourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceExecutionItemSources_WorkspaceId_SourceExecutionItemId",
                table: "SourceExecutionItemSources",
                columns: new[] { "WorkspaceId", "SourceExecutionItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceExecutionItemSources");

            migrationBuilder.DropTable(
                name: "SourceExecutionItems");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SourceExecutions_WorkspaceId_Id",
                table: "SourceExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceExecutions_ContextSnapshotJson",
                table: "SourceExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceExecutions_HistoryCounters",
                table: "SourceExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceExecutions_HistoryVersions",
                table: "SourceExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceExecutions_OutcomeCounters",
                table: "SourceExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceExecutions_TerminalCounters",
                table: "SourceExecutions");

            migrationBuilder.DropColumn(
                name: "ContextSnapshotJson",
                table: "SourceExecutions");

            migrationBuilder.DropColumn(
                name: "ContractVersion",
                table: "SourceExecutions");

            migrationBuilder.DropColumn(
                name: "HistoryVersion",
                table: "SourceExecutions");

            migrationBuilder.DropColumn(
                name: "ItemsCancelled",
                table: "SourceExecutions");

            migrationBuilder.DropColumn(
                name: "ItemsNotProcessed",
                table: "SourceExecutions");

            migrationBuilder.DropColumn(
                name: "ItemsRejected",
                table: "SourceExecutions");

            migrationBuilder.DropColumn(
                name: "ItemsRolledBack",
                table: "SourceExecutions");

            migrationBuilder.DropColumn(
                name: "NormalizationVersion",
                table: "SourceExecutions");

            migrationBuilder.DropColumn(
                name: "TargetPipelineId",
                table: "SourceExecutions");

            migrationBuilder.DropColumn(
                name: "TargetPipelineStageId",
                table: "SourceExecutions");
        }
    }
}
