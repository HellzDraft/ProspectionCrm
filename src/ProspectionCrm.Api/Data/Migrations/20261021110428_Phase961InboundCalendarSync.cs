using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase961InboundCalendarSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations");

            migrationBuilder.DropIndex(
                name: "UX_CalendarEvents_Workspace_Provider_ExternalEvent",
                table: "CalendarEvents");

            migrationBuilder.AddColumn<Guid>(
                name: "CalendarId",
                table: "CalendarEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalContentSha256",
                table: "CalendarEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExternalRevision",
                table: "CalendarEvents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OccurrenceId",
                table: "CalendarEvents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeriesId",
                table: "CalendarEvents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusCode",
                table: "CalendarEvents",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CalendarAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalAccountId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StatusCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarAccounts", x => x.Id);
                    table.UniqueConstraint("AK_CalendarAccounts_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.UniqueConstraint("AK_CalendarAccounts_WorkspaceId_Id_ConnectionId", x => new { x.WorkspaceId, x.Id, x.ConnectionId });
                    table.UniqueConstraint("AK_CalendarAccounts_WorkspaceId_Id_ConnectionId_ProviderCode", x => new { x.WorkspaceId, x.Id, x.ConnectionId, x.ProviderCode });
                    table.CheckConstraint("CK_CalendarAccounts_Identity", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid\r\nAND \"ConnectionId\" <> '00000000-0000-0000-0000-000000000000'::uuid\r\nAND \"ProviderCode\" ~ '^[a-z][a-z0-9-]{0,49}$'\r\nAND length(btrim(\"ExternalAccountId\")) > 0");
                    table.CheckConstraint("CK_CalendarAccounts_Revision", "\"Revision\" >= 0");
                    table.CheckConstraint("CK_CalendarAccounts_Status", "\"StatusCode\" IN ('active','disabled','revoked')");
                    table.ForeignKey(
                        name: "FK_CalendarAccounts_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Calendars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CalendarAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalCalendarId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StatusCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Calendars", x => x.Id);
                    table.UniqueConstraint("AK_Calendars_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.UniqueConstraint("AK_Calendars_WorkspaceId_Id_ConnectionId", x => new { x.WorkspaceId, x.Id, x.ConnectionId });
                    table.UniqueConstraint("AK_Calendars_WorkspaceId_Id_ProviderCode", x => new { x.WorkspaceId, x.Id, x.ProviderCode });
                    table.CheckConstraint("CK_Calendars_Identity", "length(btrim(\"ExternalCalendarId\")) > 0 AND \"Revision\" >= 0");
                    table.CheckConstraint("CK_Calendars_Status", "\"StatusCode\" IN ('active','disabled')");
                    table.ForeignKey(
                        name: "FK_Calendars_CalendarAccounts_WorkspaceId_CalendarAccountId_Co~",
                        columns: x => new { x.WorkspaceId, x.CalendarAccountId, x.ConnectionId, x.ProviderCode },
                        principalTable: "CalendarAccounts",
                        principalColumns: new[] { "WorkspaceId", "Id", "ConnectionId", "ProviderCode" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalendarSyncJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CalendarId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerTypeCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PreviousCursor = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    NextCursor = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    StateRevision = table.Column<long>(type: "bigint", nullable: false),
                    CalendarRevision = table.Column<long>(type: "bigint", nullable: false),
                    AccountRevision = table.Column<long>(type: "bigint", nullable: false),
                    MaxItems = table.Column<int>(type: "integer", nullable: false),
                    IntegrationOperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConflictingExternalEventId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedCount = table.Column<int>(type: "integer", nullable: false),
                    ExistingCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarSyncJobs", x => x.Id);
                    table.CheckConstraint("CK_CalendarSyncJobs_Contract", "\"OperationType\" = 'calendar.sync' AND \"TriggerTypeCode\" = 'manual' AND \"MaxItems\" BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_CalendarSyncJobs_Counts", "\"UpdatedCount\" >= 0 AND \"CreatedCount\" >= 0 AND \"ExistingCount\" >= 0 AND \"UpdatedCount\" + \"CreatedCount\" + \"ExistingCount\" <= \"MaxItems\"");
                    table.CheckConstraint("CK_CalendarSyncJobs_Dates", "(\"StartedAt\" IS NULL OR \"StartedAt\" >= \"EnqueuedAt\")\r\nAND (\"FinishedAt\" IS NULL OR \"FinishedAt\" >= COALESCE(\"StartedAt\", \"EnqueuedAt\"))");
                    table.CheckConstraint("CK_CalendarSyncJobs_Revision", "\"StateRevision\" >= 0 AND \"AccountRevision\" >= 0 AND \"CalendarRevision\" >= 0");
                    table.CheckConstraint("CK_CalendarSyncJobs_State", "(\"StatusCode\" = 'queued' AND \"StartedAt\" IS NULL AND \"FinishedAt\" IS NULL AND \"ErrorCode\" IS NULL)\r\nOR (\"StatusCode\" IN ('running','awaiting-result','result-received') AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NULL AND \"ErrorCode\" IS NULL)\r\nOR (\"StatusCode\" = 'dispatch-unknown' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NULL AND \"ErrorCode\" IS NOT NULL AND \"ErrorCode\" = 'CalendarDispatchUnknown')\r\nOR (\"StatusCode\" = 'succeeded' AND \"StartedAt\" IS NOT NULL AND \"FinishedAt\" IS NOT NULL AND \"ErrorCode\" IS NULL)\r\nOR (\"StatusCode\" IN ('failed','cancelled') AND \"FinishedAt\" IS NOT NULL AND \"ErrorCode\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_CalendarSyncJobs_Calendars_WorkspaceId_CalendarId_Connectio~",
                        columns: x => new { x.WorkspaceId, x.CalendarId, x.ConnectionId },
                        principalTable: "Calendars",
                        principalColumns: new[] { "WorkspaceId", "Id", "ConnectionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalendarSyncJobs_IntegrationOperations_IntegrationOperation~",
                        column: x => x.IntegrationOperationId,
                        principalTable: "IntegrationOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalendarSyncStates",
                columns: table => new
                {
                    CalendarId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cursor = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LastSuccessfulSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarSyncStates", x => x.CalendarId);
                    table.CheckConstraint("CK_CalendarSyncStates_Revision", "\"Revision\" >= 0");
                    table.ForeignKey(
                        name: "FK_CalendarSyncStates_Calendars_WorkspaceId_CalendarId",
                        columns: x => new { x.WorkspaceId, x.CalendarId },
                        principalTable: "Calendars",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations",
                sql: "\"Version\" = 1 AND ((\"Type\" = 'technical.probe' AND \"ProbeId\" <> '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"Type\" IN ('collection.request','email.sync','email.send','calendar.sync') AND \"ConnectionId\" IS NOT NULL AND \"ProbeId\" = '00000000-0000-0000-0000-000000000000'::uuid))");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CalendarId_ExternalEventId",
                table: "CalendarEvents",
                columns: new[] { "CalendarId", "ExternalEventId" },
                unique: true,
                filter: "\"CalendarId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CalendarId_SeriesId_OccurrenceId",
                table: "CalendarEvents",
                columns: new[] { "CalendarId", "SeriesId", "OccurrenceId" },
                unique: true,
                filter: "\"CalendarId\" IS NOT NULL AND \"SeriesId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_WorkspaceId_CalendarId_ProviderCode",
                table: "CalendarEvents",
                columns: new[] { "WorkspaceId", "CalendarId", "ProviderCode" });

            migrationBuilder.CreateIndex(
                name: "UX_CalendarEvents_Workspace_Provider_ExternalEvent",
                table: "CalendarEvents",
                columns: new[] { "WorkspaceId", "ProviderCode", "ExternalEventId" },
                unique: true,
                filter: "\"ExternalEventId\" IS NOT NULL AND \"CalendarId\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CalendarEvents_Imported",
                table: "CalendarEvents",
                sql: "\"CalendarId\" IS NULL OR (\"ExternalEventId\" IS NOT NULL AND length(btrim(\"ExternalEventId\")) > 0\r\nAND \"ExternalRevision\" IS NOT NULL AND \"ExternalRevision\" >= 0\r\nAND \"ExternalContentSha256\" IS NOT NULL AND \"ExternalContentSha256\" ~ '^[a-f0-9]{64}$'\r\nAND \"StatusCode\" IS NOT NULL AND \"StatusCode\" IN ('confirmed','cancelled')\r\nAND \"EndsAt\" > \"StartsAt\" AND ((\"SeriesId\" IS NULL AND \"OccurrenceId\" IS NULL)\r\nOR (\"SeriesId\" IS NOT NULL AND \"OccurrenceId\" IS NOT NULL)))");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarAccounts_WorkspaceId_ProviderCode_ExternalAccountId",
                table: "CalendarAccounts",
                columns: new[] { "WorkspaceId", "ProviderCode", "ExternalAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_CalendarAccountId_ExternalCalendarId",
                table: "Calendars",
                columns: new[] { "CalendarAccountId", "ExternalCalendarId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_WorkspaceId_CalendarAccountId_ConnectionId_Provid~",
                table: "Calendars",
                columns: new[] { "WorkspaceId", "CalendarAccountId", "ConnectionId", "ProviderCode" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSyncJobs_IntegrationOperationId",
                table: "CalendarSyncJobs",
                column: "IntegrationOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSyncJobs_WorkspaceId_CalendarId_ConnectionId",
                table: "CalendarSyncJobs",
                columns: new[] { "WorkspaceId", "CalendarId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSyncJobs_WorkspaceId_EnqueuedAt",
                table: "CalendarSyncJobs",
                columns: new[] { "WorkspaceId", "EnqueuedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_CalendarSyncJobs_ActiveCalendar",
                table: "CalendarSyncJobs",
                column: "CalendarId",
                unique: true,
                filter: "\"StatusCode\" IN ('queued','running','awaiting-result','dispatch-unknown','result-received')");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSyncStates_WorkspaceId_CalendarId",
                table: "CalendarSyncStates",
                columns: new[] { "WorkspaceId", "CalendarId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarEvents_Calendars_WorkspaceId_CalendarId_ProviderCode",
                table: "CalendarEvents",
                columns: new[] { "WorkspaceId", "CalendarId", "ProviderCode" },
                principalTable: "Calendars",
                principalColumns: new[] { "WorkspaceId", "Id", "ProviderCode" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql("""
                ALTER TABLE "CalendarSyncJobs" ADD CONSTRAINT "FK_CalendarSyncJobs_OperationBinding"
                FOREIGN KEY ("WorkspaceId","ConnectionId","IntegrationOperationId","OperationType")
                REFERENCES "IntegrationOperations" ("WorkspaceId","ConnectionId","Id","Type") ON DELETE RESTRICT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "CalendarAccounts") OR EXISTS (SELECT 1 FROM "Calendars")
                        OR EXISTS (SELECT 1 FROM "CalendarSyncJobs") OR EXISTS (SELECT 1 FROM "CalendarSyncStates")
                        OR EXISTS (SELECT 1 FROM "CalendarEvents" WHERE "CalendarId" IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM "IntegrationOperations" WHERE "Type"='calendar.sync') THEN
                        RAISE EXCEPTION 'Phase961 downgrade requires preserving and explicitly resolving calendar sync data first';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_CalendarEvents_Calendars_WorkspaceId_CalendarId_ProviderCode",
                table: "CalendarEvents");

            migrationBuilder.DropTable(
                name: "CalendarSyncJobs");

            migrationBuilder.DropTable(
                name: "CalendarSyncStates");

            migrationBuilder.DropTable(
                name: "Calendars");

            migrationBuilder.DropTable(
                name: "CalendarAccounts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_CalendarId_ExternalEventId",
                table: "CalendarEvents");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_CalendarId_SeriesId_OccurrenceId",
                table: "CalendarEvents");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_WorkspaceId_CalendarId_ProviderCode",
                table: "CalendarEvents");

            migrationBuilder.DropIndex(
                name: "UX_CalendarEvents_Workspace_Provider_ExternalEvent",
                table: "CalendarEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CalendarEvents_Imported",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "CalendarId",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "ExternalContentSha256",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "ExternalRevision",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "OccurrenceId",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "StatusCode",
                table: "CalendarEvents");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Contract",
                table: "IntegrationOperations",
                sql: "\"Version\" = 1 AND ((\"Type\" = 'technical.probe' AND \"ProbeId\" <> '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"Type\" IN ('collection.request','email.sync','email.send') AND \"ConnectionId\" IS NOT NULL AND \"ProbeId\" = '00000000-0000-0000-0000-000000000000'::uuid))");

            migrationBuilder.CreateIndex(
                name: "UX_CalendarEvents_Workspace_Provider_ExternalEvent",
                table: "CalendarEvents",
                columns: new[] { "WorkspaceId", "ProviderCode", "ExternalEventId" },
                unique: true,
                filter: "\"ExternalEventId\" IS NOT NULL");
        }
    }
}
