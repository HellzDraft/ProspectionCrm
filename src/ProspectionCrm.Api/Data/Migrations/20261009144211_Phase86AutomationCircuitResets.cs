using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase86AutomationCircuitResets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutomationCircuitResets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResetAfterOutcomeSequence = table.Column<long>(type: "bigint", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationCircuitResets", x => x.Id);
                    table.CheckConstraint("CK_AutomationCircuitResets_Sequence", "\"ResetAfterOutcomeSequence\" > 0");
                    table.ForeignKey(
                        name: "FK_AutomationCircuitResets_UserAccounts_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AutomationCircuitResets_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationCircuitResets_RequestedByUserId",
                table: "AutomationCircuitResets",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationCircuitResets_WorkspaceId_RequestedAt_Id",
                table: "AutomationCircuitResets",
                columns: new[] { "WorkspaceId", "RequestedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationCircuitResets_WorkspaceId_ResetAfterOutcomeSequen~",
                table: "AutomationCircuitResets",
                columns: new[] { "WorkspaceId", "ResetAfterOutcomeSequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationCircuitResets");
        }
    }
}
