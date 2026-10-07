using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase74CollectionScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceCollectionSchedules",
                columns: table => new
                {
                    SavedSearchId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uuid", nullable: false),
                    PipelineStageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    DailyUtcMinute = table.Column<int>(type: "integer", nullable: false),
                    NextCollectionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceCollectionSchedules", x => x.SavedSearchId);
                    table.CheckConstraint("CK_SourceCollectionSchedules_Minute", "\"DailyUtcMinute\" BETWEEN 0 AND 1439");
                    table.CheckConstraint("CK_SourceCollectionSchedules_State", "(\"Enabled\" AND \"NextCollectionAt\" IS NOT NULL) OR (NOT \"Enabled\" AND \"NextCollectionAt\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_SourceCollectionSchedules_PipelineStages_PipelineId_Pipelin~",
                        columns: x => new { x.PipelineId, x.PipelineStageId },
                        principalTable: "PipelineStages",
                        principalColumns: new[] { "PipelineId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceCollectionSchedules_Pipelines_WorkspaceId_PipelineId",
                        columns: x => new { x.WorkspaceId, x.PipelineId },
                        principalTable: "Pipelines",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceCollectionSchedules_SavedSearches_WorkspaceId_SavedSe~",
                        columns: x => new { x.WorkspaceId, x.SavedSearchId, x.PipelineId },
                        principalTable: "SavedSearches",
                        principalColumns: new[] { "WorkspaceId", "Id", "PipelineId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionSchedules_NextCollectionAt_SavedSearchId",
                table: "SourceCollectionSchedules",
                columns: new[] { "NextCollectionAt", "SavedSearchId" },
                filter: "\"Enabled\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionSchedules_PipelineId_PipelineStageId",
                table: "SourceCollectionSchedules",
                columns: new[] { "PipelineId", "PipelineStageId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionSchedules_WorkspaceId_PipelineId",
                table: "SourceCollectionSchedules",
                columns: new[] { "WorkspaceId", "PipelineId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionSchedules_WorkspaceId_SavedSearchId_Pipelin~",
                table: "SourceCollectionSchedules",
                columns: new[] { "WorkspaceId", "SavedSearchId", "PipelineId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceCollectionSchedules");
        }
    }
}
