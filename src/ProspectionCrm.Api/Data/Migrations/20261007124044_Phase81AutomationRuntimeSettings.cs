using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase81AutomationRuntimeSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomationRuntimeSettings",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    OperatingModeCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "manual"),
                    MaxExecutionsPerMinute = table.Column<int>(type: "integer", nullable: false, defaultValue: 10),
                    MaxExecutionsPerDay = table.Column<int>(type: "integer", nullable: false, defaultValue: 100),
                    MaxConsecutiveFailures = table.Column<int>(type: "integer", nullable: false, defaultValue: 3),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuntimeSettings", x => x.WorkspaceId);
                    table.CheckConstraint("CK_AutomationRuntimeSettings_Failures", "\"MaxConsecutiveFailures\" BETWEEN 1 AND 20");
                    table.CheckConstraint("CK_AutomationRuntimeSettings_Mode", "\"OperatingModeCode\" IN ('manual', 'assist', 'automatic')");
                    table.CheckConstraint("CK_AutomationRuntimeSettings_PerDay", "\"MaxExecutionsPerDay\" BETWEEN 1 AND 10000 AND \"MaxExecutionsPerDay\" >= \"MaxExecutionsPerMinute\"");
                    table.CheckConstraint("CK_AutomationRuntimeSettings_PerMinute", "\"MaxExecutionsPerMinute\" BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "FK_AutomationRuntimeSettings_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
            // Existing installations receive the same safe defaults as a new bootstrap.
            migrationBuilder.Sql("""
                INSERT INTO "AutomationRuntimeSettings" ("WorkspaceId")
                SELECT "Id" FROM "Workspaces";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationRuntimeSettings");
        }
    }
}
