using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase932IntegrationConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConnectionId",
                table: "IntegrationOperations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntegrationOperations_Connection",
                table: "IntegrationOperations",
                sql: "\"ConnectionId\" IS NULL OR \"ConnectionId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_IntegrationOperations_Connection",
                table: "IntegrationOperations");

            migrationBuilder.DropColumn(
                name: "ConnectionId",
                table: "IntegrationOperations");
        }
    }
}
