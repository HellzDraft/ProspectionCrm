using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase71PersistentCollectionJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_SavedSearches_WorkspaceId_Id_PipelineId",
                table: "SavedSearches",
                columns: new[] { "WorkspaceId", "Id", "PipelineId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_PipelineStages_PipelineId_Id",
                table: "PipelineStages",
                columns: new[] { "PipelineId", "Id" });

            migrationBuilder.CreateTable(
                name: "SourceCollectionJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SavedSearchId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineStageId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerTypeCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    SourceExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceCollectionJobs", x => x.Id);
                    table.CheckConstraint("CK_SourceCollectionJobs_AttemptCount", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_SourceCollectionJobs_AvailableAt", "\"AvailableAt\" >= \"EnqueuedAt\"");
                    table.CheckConstraint("CK_SourceCollectionJobs_FinishedAt", "\"FinishedAt\" IS NULL OR \"StartedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\"");
                    table.CheckConstraint("CK_SourceCollectionJobs_StartedAt", "\"StartedAt\" IS NULL OR \"StartedAt\" >= \"AvailableAt\"");
                    table.CheckConstraint("CK_SourceCollectionJobs_State", "(\"StatusCode\" = 'queued' AND \"StartedAt\" IS NULL AND \"FinishedAt\" IS NULL AND \"SourceExecutionId\" IS NULL AND \"ErrorCode\" IS NULL)\nOR (\"StatusCode\" = 'running' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NULL AND \"ErrorCode\" IS NULL)\nOR (\"StatusCode\" = 'succeeded' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NOT NULL AND \"SourceExecutionId\" IS NOT NULL AND \"ErrorCode\" IS NULL)\nOR (\"StatusCode\" = 'failed' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NOT NULL AND \"ErrorCode\" IS NOT NULL)\nOR (\"StatusCode\" = 'cancelled' AND \"FinishedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_SourceCollectionJobs_StatusCode", "\"StatusCode\" IN ('queued', 'running', 'succeeded', 'failed', 'cancelled')");
                    table.CheckConstraint("CK_SourceCollectionJobs_TriggerTypeCode", "\"TriggerTypeCode\" IN ('manual', 'scheduled', 'event', 'retry')");
                    table.ForeignKey(
                        name: "FK_SourceCollectionJobs_PipelineStages_PipelineId_PipelineStag~",
                        columns: x => new { x.PipelineId, x.PipelineStageId },
                        principalTable: "PipelineStages",
                        principalColumns: new[] { "PipelineId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceCollectionJobs_Pipelines_WorkspaceId_PipelineId",
                        columns: x => new { x.WorkspaceId, x.PipelineId },
                        principalTable: "Pipelines",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceCollectionJobs_SavedSearches_WorkspaceId_SavedSearchI~",
                        columns: x => new { x.WorkspaceId, x.SavedSearchId, x.PipelineId },
                        principalTable: "SavedSearches",
                        principalColumns: new[] { "WorkspaceId", "Id", "PipelineId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceCollectionJobs_SourceExecutions_WorkspaceId_SourceExe~",
                        columns: x => new { x.WorkspaceId, x.SourceExecutionId },
                        principalTable: "SourceExecutions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceCollectionJobs_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionJobs_PipelineId_PipelineStageId",
                table: "SourceCollectionJobs",
                columns: new[] { "PipelineId", "PipelineStageId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionJobs_SourceExecutionId",
                table: "SourceCollectionJobs",
                column: "SourceExecutionId",
                unique: true,
                filter: "\"SourceExecutionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionJobs_StatusCode_AvailableAt_EnqueuedAt_Id",
                table: "SourceCollectionJobs",
                columns: new[] { "StatusCode", "AvailableAt", "EnqueuedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionJobs_WorkspaceId_EnqueuedAt_Id",
                table: "SourceCollectionJobs",
                columns: new[] { "WorkspaceId", "EnqueuedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionJobs_WorkspaceId_PipelineId",
                table: "SourceCollectionJobs",
                columns: new[] { "WorkspaceId", "PipelineId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionJobs_WorkspaceId_SavedSearchId_PipelineId",
                table: "SourceCollectionJobs",
                columns: new[] { "WorkspaceId", "SavedSearchId", "PipelineId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionJobs_WorkspaceId_SavedSearchId_StatusCode_E~",
                table: "SourceCollectionJobs",
                columns: new[] { "WorkspaceId", "SavedSearchId", "StatusCode", "EnqueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionJobs_WorkspaceId_SourceExecutionId",
                table: "SourceCollectionJobs",
                columns: new[] { "WorkspaceId", "SourceExecutionId" });

            migrationBuilder.CreateIndex(
                name: "UX_SourceCollectionJobs_Workspace_Search_Stage_Active",
                table: "SourceCollectionJobs",
                columns: new[] { "WorkspaceId", "SavedSearchId", "PipelineStageId" },
                unique: true,
                filter: "\"StatusCode\" IN ('queued', 'running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceCollectionJobs");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SavedSearches_WorkspaceId_Id_PipelineId",
                table: "SavedSearches");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PipelineStages_PipelineId_Id",
                table: "PipelineStages");
        }
    }
}
