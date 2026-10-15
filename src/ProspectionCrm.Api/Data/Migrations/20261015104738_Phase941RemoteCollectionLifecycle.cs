using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase941RemoteCollectionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_SourceCollectionJobs_Workspace_Search_Stage_Active",
                table: "SourceCollectionJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceCollectionJobs_State",
                table: "SourceCollectionJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceCollectionJobs_StatusCode",
                table: "SourceCollectionJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceCollectionJobAttempts_State",
                table: "SourceCollectionJobAttempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationReceipts_Content",
                table: "IntegrationReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Ids",
                table: "IntegrationOperations");

            migrationBuilder.CreateTable(
                name: "RemoteCollectionIntents",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContextSnapshotJson = table.Column<string>(type: "text", nullable: false),
                    CommandJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteCollectionIntents", x => x.JobId);
                    table.CheckConstraint("CK_RemoteCollectionIntents_Command", "octet_length(\"CommandJson\") BETWEEN 1 AND 4096");
                    table.CheckConstraint("CK_RemoteCollectionIntents_Connection", "\"ConnectionId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"OperationType\" = 'collection.request'");
                    table.CheckConstraint("CK_RemoteCollectionIntents_Fingerprint", "\"Fingerprint\" ~ '^[0-9A-Fa-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_RemoteCollectionIntents_IntegrationOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "IntegrationOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RemoteCollectionIntents_SourceCollectionJobs_WorkspaceId_Jo~",
                        columns: x => new { x.WorkspaceId, x.JobId },
                        principalTable: "SourceCollectionJobs",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RemoteCollectionIntents_SourceConfigurations_WorkspaceId_So~",
                        columns: x => new { x.WorkspaceId, x.SourceConfigurationId },
                        principalTable: "SourceConfigurations",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_SourceCollectionJobs_Workspace_Search_Stage_Active",
                table: "SourceCollectionJobs",
                columns: new[] { "WorkspaceId", "SavedSearchId", "PipelineStageId" },
                unique: true,
                filter: "\"StatusCode\" IN ('queued', 'running', 'awaiting-result', 'dispatch-unknown', 'result-received')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceCollectionJobs_State",
                table: "SourceCollectionJobs",
                sql: "(\"StatusCode\" = 'queued' AND \"StartedAt\" IS NULL AND \"FinishedAt\" IS NULL AND \"SourceExecutionId\" IS NULL AND \"ErrorCode\" IS NULL)\r\nOR (\"StatusCode\" = 'running' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NULL AND \"ErrorCode\" IS NULL)\r\nOR (\"StatusCode\" IN ('awaiting-result', 'result-received') AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NULL AND \"SourceExecutionId\" IS NULL AND \"ErrorCode\" IS NULL AND \"AttemptCount\" > 0)\r\nOR (\"StatusCode\" = 'dispatch-unknown' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NULL AND \"SourceExecutionId\" IS NULL AND \"ErrorCode\" IS NOT NULL AND \"ErrorCode\" = 'RemoteDispatchUnknown' AND \"AttemptCount\" > 0)\r\nOR (\"StatusCode\" = 'succeeded' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NOT NULL AND \"SourceExecutionId\" IS NOT NULL AND \"ErrorCode\" IS NULL)\r\nOR (\"StatusCode\" = 'failed' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NOT NULL AND \"ErrorCode\" IS NOT NULL)\r\nOR (\"StatusCode\" = 'cancelled' AND \"FinishedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceCollectionJobs_StatusCode",
                table: "SourceCollectionJobs",
                sql: "\"StatusCode\" IN ('queued', 'running', 'awaiting-result', 'dispatch-unknown', 'result-received', 'succeeded', 'failed', 'cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceCollectionJobAttempts_State",
                table: "SourceCollectionJobAttempts",
                sql: "(\"StatusCode\" = 'running' AND \"FinishedAt\" IS NULL)\nOR (\"StatusCode\" IN ('awaiting-result', 'result-received') AND \"FinishedAt\" IS NULL AND \"SourceExecutionId\" IS NULL AND \"ErrorCode\" IS NULL)\nOR (\"StatusCode\" = 'dispatch-unknown' AND \"FinishedAt\" IS NULL AND \"SourceExecutionId\" IS NULL AND \"ErrorCode\" IS NOT NULL AND \"ErrorCode\" = 'RemoteDispatchUnknown')\nOR (\"StatusCode\" = 'succeeded' AND \"FinishedAt\" IS NOT NULL AND \"SourceExecutionId\" IS NOT NULL AND \"ErrorCode\" IS NULL)\nOR (\"StatusCode\" IN ('failed', 'cancelled') AND \"FinishedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationReceipts_Content",
                table: "IntegrationReceipts",
                sql: "octet_length(\"CanonicalContent\") BETWEEN 1 AND 524288");

            migrationBuilder.CreateIndex(
                name: "UX_IntegrationOperations_CollectionBinding",
                table: "IntegrationOperations",
                columns: new[] { "WorkspaceId", "ConnectionId", "Id", "Type" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations",
                sql: "\"Version\" = 1 AND ((\"Type\" = 'technical.probe' AND \"ProbeId\" <> '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"Type\" = 'collection.request' AND \"ConnectionId\" IS NOT NULL AND \"ProbeId\" = '00000000-0000-0000-0000-000000000000'::uuid))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Ids",
                table: "IntegrationOperations",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid\r\nAND \"CommandMessageId\" <> '00000000-0000-0000-0000-000000000000'::uuid\r\nAND \"CorrelationId\" <> '00000000-0000-0000-0000-000000000000'::uuid");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteCollectionIntents_OperationId",
                table: "RemoteCollectionIntents",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteCollectionIntents_WorkspaceId_JobId",
                table: "RemoteCollectionIntents",
                columns: new[] { "WorkspaceId", "JobId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteCollectionIntents_WorkspaceId_SourceConfigurationId",
                table: "RemoteCollectionIntents",
                columns: new[] { "WorkspaceId", "SourceConfigurationId" });

            // PostgreSQL permits referencing a nullable column through a unique index. Modeling
            // it as an EF alternate key would wrongly prohibit historical ConnectionId=NULL probes.
            migrationBuilder.Sql("""
                ALTER TABLE "RemoteCollectionIntents" ADD CONSTRAINT "FK_RemoteCollectionIntents_AuthorizedOperation"
                    FOREIGN KEY ("WorkspaceId", "ConnectionId", "OperationId", "OperationType")
                    REFERENCES "IntegrationOperations" ("WorkspaceId", "ConnectionId", "Id", "Type") ON DELETE RESTRICT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never discard a durable remote intent or reinterpret its job as a local collection.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "RemoteCollectionIntents")
                        OR EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE "Type" = 'collection.request') THEN
                        RAISE EXCEPTION 'Phase941 downgrade requires an empty remote collection history';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "RemoteCollectionIntents");

            migrationBuilder.DropIndex(
                name: "UX_SourceCollectionJobs_Workspace_Search_Stage_Active",
                table: "SourceCollectionJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceCollectionJobs_State",
                table: "SourceCollectionJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceCollectionJobs_StatusCode",
                table: "SourceCollectionJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceCollectionJobAttempts_State",
                table: "SourceCollectionJobAttempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationReceipts_Content",
                table: "IntegrationReceipts");

            migrationBuilder.DropIndex(
                name: "UX_IntegrationOperations_CollectionBinding",
                table: "IntegrationOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Ids",
                table: "IntegrationOperations");

            migrationBuilder.CreateIndex(
                name: "UX_SourceCollectionJobs_Workspace_Search_Stage_Active",
                table: "SourceCollectionJobs",
                columns: new[] { "WorkspaceId", "SavedSearchId", "PipelineStageId" },
                unique: true,
                filter: "\"StatusCode\" IN ('queued', 'running')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceCollectionJobs_State",
                table: "SourceCollectionJobs",
                sql: "(\"StatusCode\" = 'queued' AND \"StartedAt\" IS NULL AND \"FinishedAt\" IS NULL AND \"SourceExecutionId\" IS NULL AND \"ErrorCode\" IS NULL)\r\nOR (\"StatusCode\" = 'running' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NULL AND \"ErrorCode\" IS NULL)\r\nOR (\"StatusCode\" = 'succeeded' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NOT NULL AND \"SourceExecutionId\" IS NOT NULL AND \"ErrorCode\" IS NULL)\r\nOR (\"StatusCode\" = 'failed' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NOT NULL AND \"ErrorCode\" IS NOT NULL)\r\nOR (\"StatusCode\" = 'cancelled' AND \"FinishedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceCollectionJobs_StatusCode",
                table: "SourceCollectionJobs",
                sql: "\"StatusCode\" IN ('queued', 'running', 'succeeded', 'failed', 'cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceCollectionJobAttempts_State",
                table: "SourceCollectionJobAttempts",
                sql: "(\"StatusCode\" = 'running' AND \"FinishedAt\" IS NULL)\nOR (\"StatusCode\" = 'succeeded' AND \"FinishedAt\" IS NOT NULL AND \"SourceExecutionId\" IS NOT NULL AND \"ErrorCode\" IS NULL)\nOR (\"StatusCode\" IN ('failed', 'cancelled') AND \"FinishedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationReceipts_Content",
                table: "IntegrationReceipts",
                sql: "octet_length(\"CanonicalContent\") BETWEEN 1 AND 4096");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations",
                sql: "\"Version\" = 1 AND \"Type\" = 'technical.probe'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Ids",
                table: "IntegrationOperations",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"CommandMessageId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"CorrelationId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"ProbeId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        }
    }
}
