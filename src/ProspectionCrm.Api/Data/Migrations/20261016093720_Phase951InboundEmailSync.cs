using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase951InboundEmailSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations");

            migrationBuilder.DropIndex(
                name: "UX_EmailMessages_Workspace_Provider_ExternalMessage",
                table: "EmailMessages");

            migrationBuilder.AddColumn<Guid>(
                name: "EmailAccountId",
                table: "EmailMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmailAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalAccountId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EmailAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StatusCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailAccounts", x => x.Id);
                    table.UniqueConstraint("AK_EmailAccounts_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.UniqueConstraint("AK_EmailAccounts_WorkspaceId_Id_ConnectionId", x => new { x.WorkspaceId, x.Id, x.ConnectionId });
                    table.UniqueConstraint("AK_EmailAccounts_WorkspaceId_Id_ProviderCode", x => new { x.WorkspaceId, x.Id, x.ProviderCode });
                    table.CheckConstraint("CK_EmailAccounts_Identity", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"ConnectionId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"ProviderCode\" ~ '^[a-z][a-z0-9-]{0,49}$'\nAND length(btrim(\"ExternalAccountId\")) > 0 AND length(btrim(\"EmailAddress\")) > 0");
                    table.CheckConstraint("CK_EmailAccounts_Revision", "\"Revision\" >= 0");
                    table.CheckConstraint("CK_EmailAccounts_Status", "\"StatusCode\" IN ('active','disabled','revoked')");
                    table.ForeignKey(
                        name: "FK_EmailAccounts_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmailSyncJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerTypeCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PreviousCursor = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    NextCursor = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    StateRevision = table.Column<long>(type: "bigint", nullable: false),
                    AccountRevision = table.Column<long>(type: "bigint", nullable: false),
                    MaxItems = table.Column<int>(type: "integer", nullable: false),
                    IntegrationOperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConflictingExternalMessageId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedCount = table.Column<int>(type: "integer", nullable: false),
                    ExistingCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSyncJobs", x => x.Id);
                    table.CheckConstraint("CK_EmailSyncJobs_Contract", "\"OperationType\" = 'email.sync' AND \"TriggerTypeCode\" = 'manual' AND \"MaxItems\" BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_EmailSyncJobs_Counts", "\"CreatedCount\" >= 0 AND \"ExistingCount\" >= 0 AND \"CreatedCount\" + \"ExistingCount\" <= \"MaxItems\"");
                    table.CheckConstraint("CK_EmailSyncJobs_Dates", "(\"StartedAt\" IS NULL OR \"StartedAt\" >= \"EnqueuedAt\")\nAND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= COALESCE(\"StartedAt\", \"EnqueuedAt\"))");
                    table.CheckConstraint("CK_EmailSyncJobs_Revision", "\"StateRevision\" >= 0 AND \"AccountRevision\" >= 0");
                    table.CheckConstraint("CK_EmailSyncJobs_State", "(\"StatusCode\" = 'queued' AND \"StartedAt\" IS NULL AND \"FinishedAt\" IS NULL AND \"ErrorCode\" IS NULL)\nOR (\"StatusCode\" IN ('running','awaiting-result','result-received') AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NULL AND \"ErrorCode\" IS NULL)\nOR (\"StatusCode\" = 'dispatch-unknown' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NULL AND \"ErrorCode\" IS NOT NULL AND \"ErrorCode\" = 'EmailDispatchUnknown')\nOR (\"StatusCode\" = 'succeeded' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NOT NULL AND \"ErrorCode\" IS NULL)\nOR (\"StatusCode\" IN ('failed','cancelled') AND \"FinishedAt\" IS NOT NULL AND \"ErrorCode\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_EmailSyncJobs_EmailAccounts_WorkspaceId_EmailAccountId_Conn~",
                        columns: x => new { x.WorkspaceId, x.EmailAccountId, x.ConnectionId },
                        principalTable: "EmailAccounts",
                        principalColumns: new[] { "WorkspaceId", "Id", "ConnectionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmailSyncJobs_IntegrationOperations_IntegrationOperationId",
                        column: x => x.IntegrationOperationId,
                        principalTable: "IntegrationOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmailSyncStates",
                columns: table => new
                {
                    EmailAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cursor = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LastSuccessfulSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSyncStates", x => x.EmailAccountId);
                    table.CheckConstraint("CK_EmailSyncStates_Revision", "\"Revision\" >= 0");
                    table.ForeignKey(
                        name: "FK_EmailSyncStates_EmailAccounts_WorkspaceId_EmailAccountId",
                        columns: x => new { x.WorkspaceId, x.EmailAccountId },
                        principalTable: "EmailAccounts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations",
                sql: "\"Version\" = 1 AND ((\"Type\" = 'technical.probe' AND \"ProbeId\" <> '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"Type\" IN ('collection.request','email.sync') AND \"ConnectionId\" IS NOT NULL AND \"ProbeId\" = '00000000-0000-0000-0000-000000000000'::uuid))");

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_WorkspaceId_EmailAccountId_ProviderCode",
                table: "EmailMessages",
                columns: new[] { "WorkspaceId", "EmailAccountId", "ProviderCode" });

            migrationBuilder.CreateIndex(
                name: "UX_EmailMessages_Account_ExternalMessage",
                table: "EmailMessages",
                columns: new[] { "WorkspaceId", "EmailAccountId", "ExternalMessageId" },
                unique: true,
                filter: "\"EmailAccountId\" IS NOT NULL AND \"ExternalMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_EmailMessages_Workspace_Provider_ExternalMessage",
                table: "EmailMessages",
                columns: new[] { "WorkspaceId", "ProviderCode", "ExternalMessageId" },
                unique: true,
                filter: "\"ExternalMessageId\" IS NOT NULL AND \"EmailAccountId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmailAccounts_WorkspaceId_ProviderCode_ExternalAccountId",
                table: "EmailAccounts",
                columns: new[] { "WorkspaceId", "ProviderCode", "ExternalAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailSyncJobs_IntegrationOperationId",
                table: "EmailSyncJobs",
                column: "IntegrationOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailSyncJobs_WorkspaceId_EmailAccountId_ConnectionId",
                table: "EmailSyncJobs",
                columns: new[] { "WorkspaceId", "EmailAccountId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailSyncJobs_WorkspaceId_EnqueuedAt",
                table: "EmailSyncJobs",
                columns: new[] { "WorkspaceId", "EnqueuedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_EmailSyncJobs_ActiveAccount",
                table: "EmailSyncJobs",
                column: "EmailAccountId",
                unique: true,
                filter: "\"StatusCode\" IN ('queued','running','awaiting-result','dispatch-unknown','result-received')");

            migrationBuilder.CreateIndex(
                name: "IX_EmailSyncStates_WorkspaceId_EmailAccountId",
                table: "EmailSyncStates",
                columns: new[] { "WorkspaceId", "EmailAccountId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_EmailMessages_EmailAccounts_WorkspaceId_EmailAccountId_Prov~",
                table: "EmailMessages",
                columns: new[] { "WorkspaceId", "EmailAccountId", "ProviderCode" },
                principalTable: "EmailAccounts",
                principalColumns: new[] { "WorkspaceId", "Id", "ProviderCode" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                ALTER TABLE "EmailSyncJobs" ADD CONSTRAINT "FK_EmailSyncJobs_OperationBinding"
                FOREIGN KEY ("WorkspaceId", "ConnectionId", "IntegrationOperationId", "OperationType")
                REFERENCES "IntegrationOperations" ("WorkspaceId", "ConnectionId", "Id", "Type") ON DELETE RESTRICT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuse to erase account identities, cursors or durable sync history on downgrade.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "EmailAccounts") OR EXISTS (SELECT 1 FROM "EmailSyncJobs")
                        OR EXISTS (SELECT 1 FROM "EmailSyncStates")
                        OR EXISTS (SELECT 1 FROM "EmailMessages" WHERE "EmailAccountId" IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE "Type" = 'email.sync') THEN
                        RAISE EXCEPTION 'Phase951 downgrade requires preserving and explicitly resolving email sync data first';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_EmailMessages_EmailAccounts_WorkspaceId_EmailAccountId_Prov~",
                table: "EmailMessages");

            migrationBuilder.DropTable(
                name: "EmailSyncJobs");

            migrationBuilder.DropTable(
                name: "EmailSyncStates");

            migrationBuilder.DropTable(
                name: "EmailAccounts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations");

            migrationBuilder.DropIndex(
                name: "IX_EmailMessages_WorkspaceId_EmailAccountId_ProviderCode",
                table: "EmailMessages");

            migrationBuilder.DropIndex(
                name: "UX_EmailMessages_Account_ExternalMessage",
                table: "EmailMessages");

            migrationBuilder.DropIndex(
                name: "UX_EmailMessages_Workspace_Provider_ExternalMessage",
                table: "EmailMessages");

            migrationBuilder.DropColumn(
                name: "EmailAccountId",
                table: "EmailMessages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations",
                sql: "\"Version\" = 1 AND ((\"Type\" = 'technical.probe' AND \"ProbeId\" <> '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"Type\" = 'collection.request' AND \"ConnectionId\" IS NOT NULL AND \"ProbeId\" = '00000000-0000-0000-0000-000000000000'::uuid))");

            migrationBuilder.CreateIndex(
                name: "UX_EmailMessages_Workspace_Provider_ExternalMessage",
                table: "EmailMessages",
                columns: new[] { "WorkspaceId", "ProviderCode", "ExternalMessageId" },
                unique: true,
                filter: "\"ExternalMessageId\" IS NOT NULL");
        }
    }
}
