using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase931IntegrationPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrationOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProbeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationOperations", x => x.Id);
                    table.CheckConstraint("CK_IntegrationOperations_Completion", "(\"StatusCode\" IN ('succeeded','failed')) = (\"CompletedAt\" IS NOT NULL)\nAND (\"CompletedAt\" IS NULL OR \"CompletedAt\" >= \"CreatedAt\")");
                    table.CheckConstraint("CK_IntegrationOperations_Contract", "\"Version\" = 1 AND \"Type\" = 'technical.probe'");
                    table.CheckConstraint("CK_IntegrationOperations_Ids", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"CommandMessageId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"CorrelationId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"ProbeId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_IntegrationOperations_Revision", "\"Revision\" >= 0");
                    table.CheckConstraint("CK_IntegrationOperations_Status", "\"StatusCode\" IN ('pending','awaiting-result','result-received','succeeded','failed')");
                    table.ForeignKey(
                        name: "FK_IntegrationOperations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationAttempts", x => x.Id);
                    table.CheckConstraint("CK_IntegrationAttempts_Completion", "(\"StatusCode\" = 'started') = (\"FinishedAt\" IS NULL)\nAND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= \"StartedAt\")");
                    table.CheckConstraint("CK_IntegrationAttempts_Number", "\"Number\" > 0");
                    table.CheckConstraint("CK_IntegrationAttempts_Status", "\"StatusCode\" IN ('started','acknowledged','failed','unknown')");
                    table.ForeignKey(
                        name: "FK_IntegrationAttempts_IntegrationOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "IntegrationOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationReceipts",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CanonicalContent = table.Column<string>(type: "text", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationReceipts", x => x.MessageId);
                    table.CheckConstraint("CK_IntegrationReceipts_Content", "octet_length(\"CanonicalContent\") BETWEEN 1 AND 4096");
                    table.CheckConstraint("CK_IntegrationReceipts_Dates", "\"ProcessedAt\" IS NULL OR \"ProcessedAt\" >= \"ReceivedAt\"");
                    table.CheckConstraint("CK_IntegrationReceipts_Hash", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_IntegrationReceipts_Id", "\"MessageId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_IntegrationReceipts_IntegrationOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "IntegrationOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationAttempts_OperationId_Number",
                table: "IntegrationAttempts",
                columns: new[] { "OperationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_IntegrationAttempts_Active",
                table: "IntegrationAttempts",
                column: "OperationId",
                unique: true,
                filter: "\"StatusCode\" = 'started'");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationOperations_CommandMessageId",
                table: "IntegrationOperations",
                column: "CommandMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationOperations_WorkspaceId_StatusCode_CreatedAt",
                table: "IntegrationOperations",
                columns: new[] { "WorkspaceId", "StatusCode", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationReceipts_OperationId",
                table: "IntegrationReceipts",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationReceipts_ReceivedAt_MessageId",
                table: "IntegrationReceipts",
                columns: new[] { "ReceivedAt", "MessageId" },
                filter: "\"ProcessedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationAttempts");

            migrationBuilder.DropTable(
                name: "IntegrationReceipts");

            migrationBuilder.DropTable(
                name: "IntegrationOperations");
        }
    }
}
