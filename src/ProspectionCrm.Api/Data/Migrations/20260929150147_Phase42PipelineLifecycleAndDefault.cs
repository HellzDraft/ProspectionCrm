using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase42PipelineLifecycleAndDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultPipelineId",
                table: "Workspaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsVisible",
                table: "Pipelines",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Pipelines_WorkspaceId_Id",
                table: "Pipelines",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_Id_DefaultPipelineId",
                table: "Workspaces",
                columns: new[] { "Id", "DefaultPipelineId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Workspaces_Pipelines_Id_DefaultPipelineId",
                table: "Workspaces",
                columns: new[] { "Id", "DefaultPipelineId" },
                principalTable: "Pipelines",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Workspaces_Pipelines_Id_DefaultPipelineId",
                table: "Workspaces");

            migrationBuilder.DropIndex(
                name: "IX_Workspaces_Id_DefaultPipelineId",
                table: "Workspaces");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Pipelines_WorkspaceId_Id",
                table: "Pipelines");

            migrationBuilder.DropColumn(
                name: "DefaultPipelineId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "IsVisible",
                table: "Pipelines");
        }
    }
}
