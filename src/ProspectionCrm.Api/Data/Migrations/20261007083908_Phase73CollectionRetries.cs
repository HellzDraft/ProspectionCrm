using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase73CollectionRetries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_SourceCollectionJobs_WorkspaceId_Id",
                table: "SourceCollectionJobs",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateTable(
                name: "SourceCollectionJobAttempts",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StatusCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpstreamStatusCode = table.Column<int>(type: "integer", nullable: true),
                    SourceExecutionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceCollectionJobAttempts", x => new { x.JobId, x.AttemptNumber });
                    table.CheckConstraint("CK_SourceCollectionJobAttempts_Dates", "\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\"");
                    table.CheckConstraint("CK_SourceCollectionJobAttempts_Http", "\"UpstreamStatusCode\" IS NULL OR \"UpstreamStatusCode\" BETWEEN 100 AND 599");
                    table.CheckConstraint("CK_SourceCollectionJobAttempts_Number", "\"AttemptNumber\" >= 0");
                    table.CheckConstraint("CK_SourceCollectionJobAttempts_State", "(\"StatusCode\" = 'running' AND \"FinishedAt\" IS NULL)\nOR (\"StatusCode\" = 'succeeded' AND \"FinishedAt\" IS NOT NULL AND \"SourceExecutionId\" IS NOT NULL AND \"ErrorCode\" IS NULL)\nOR (\"StatusCode\" IN ('failed', 'cancelled') AND \"FinishedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_SourceCollectionJobAttempts_SourceCollectionJobs_WorkspaceI~",
                        columns: x => new { x.WorkspaceId, x.JobId },
                        principalTable: "SourceCollectionJobs",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceCollectionJobAttempts_SourceExecutions_WorkspaceId_So~",
                        columns: x => new { x.WorkspaceId, x.SourceExecutionId },
                        principalTable: "SourceExecutions",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            // Preserve known legacy attempts; do not invent earlier attempts or change existing rows.
            migrationBuilder.Sql("""
                INSERT INTO "SourceCollectionJobAttempts" ("JobId", "WorkspaceId", "AttemptNumber", "StartedAt",
                    "FinishedAt", "StatusCode", "ErrorCode", "SourceExecutionId")
                SELECT j."Id", j."WorkspaceId", j."AttemptCount", COALESCE(j."StartedAt", j."EnqueuedAt"),
                    CASE WHEN j."StatusCode" = 'running' THEN NULL
                        ELSE GREATEST(j."FinishedAt", COALESCE(j."StartedAt", j."EnqueuedAt")) END,
                    j."StatusCode",
                    CASE WHEN j."ErrorCode" IS NULL THEN NULL
                        WHEN j."ErrorCode" IN ('UpstreamTimeout', 'UpstreamTransportError', 'DnsResolutionFailed',
                            'UpstreamHttpError', 'CollectionWorkerStopping', 'CollectionLeaseExpired') THEN j."ErrorCode"
                        ELSE 'CollectionWorkerError' END,
                    j."SourceExecutionId"
                FROM "SourceCollectionJobs" j
                WHERE j."StatusCode" <> 'queued'
                    AND (j."StartedAt" IS NOT NULL OR j."AttemptCount" > 0 OR j."SourceExecutionId" IS NOT NULL)
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionJobAttempts_SourceExecutionId",
                table: "SourceCollectionJobAttempts",
                column: "SourceExecutionId",
                unique: true,
                filter: "\"SourceExecutionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionJobAttempts_WorkspaceId_JobId",
                table: "SourceCollectionJobAttempts",
                columns: new[] { "WorkspaceId", "JobId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceCollectionJobAttempts_WorkspaceId_SourceExecutionId",
                table: "SourceCollectionJobAttempts",
                columns: new[] { "WorkspaceId", "SourceExecutionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceCollectionJobAttempts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SourceCollectionJobs_WorkspaceId_Id",
                table: "SourceCollectionJobs");
        }
    }
}
