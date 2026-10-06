using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase622PersistentSourceIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "OpportunitySources",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedSourceUrl",
                table: "OpportunitySources",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.Sql(NormalizationSql);
            migrationBuilder.Sql(BackfillSql);
            migrationBuilder.AlterColumn<Guid>(
                name: "WorkspaceId", table: "OpportunitySources", type: "uuid", nullable: false,
                oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SourceConfigurations_WorkspaceId_Id",
                table: "SourceConfigurations",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SavedSearches_WorkspaceId_Id",
                table: "SavedSearches",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_OpportunitySources_WorkspaceId_Id",
                table: "OpportunitySources",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Opportunities_WorkspaceId_Id",
                table: "Opportunities",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.DropForeignKey(
                name: "FK_OpportunitySources_Opportunities_OpportunityId",
                table: "OpportunitySources");

            migrationBuilder.DropForeignKey(
                name: "FK_OpportunitySources_SavedSearches_SavedSearchId",
                table: "OpportunitySources");

            migrationBuilder.DropForeignKey(
                name: "FK_OpportunitySources_SourceConfigurations_SourceConfiguration~",
                table: "OpportunitySources");

            migrationBuilder.DropForeignKey(
                name: "FK_OpportunitySources_SourceExecutions_SourceExecutionId",
                table: "OpportunitySources");

            migrationBuilder.AddForeignKey(
                name: "FK_OpportunitySources_Opportunities_WorkspaceId_OpportunityId",
                table: "OpportunitySources",
                columns: new[] { "WorkspaceId", "OpportunityId" },
                principalTable: "Opportunities",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_OpportunitySources_SavedSearches_WorkspaceId_SavedSearchId",
                table: "OpportunitySources",
                columns: new[] { "WorkspaceId", "SavedSearchId" },
                principalTable: "SavedSearches",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OpportunitySources_SourceConfigurations_WorkspaceId_SourceC~",
                table: "OpportunitySources",
                columns: new[] { "WorkspaceId", "SourceConfigurationId" },
                principalTable: "SourceConfigurations",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OpportunitySources_SourceExecutions_WorkspaceId_SourceExecu~",
                table: "OpportunitySources",
                columns: new[] { "WorkspaceId", "SourceExecutionId" },
                principalTable: "SourceExecutions",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OpportunitySources_Workspaces_WorkspaceId",
                table: "OpportunitySources",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropIndex(
                name: "IX_OpportunitySources_OpportunityId",
                table: "OpportunitySources");

            migrationBuilder.DropIndex(
                name: "IX_OpportunitySources_SavedSearchId",
                table: "OpportunitySources");

            migrationBuilder.DropIndex(
                name: "IX_OpportunitySources_SourceConfigurationId",
                table: "OpportunitySources");

            migrationBuilder.DropIndex(
                name: "IX_OpportunitySources_SourceExecutionId",
                table: "OpportunitySources");

            migrationBuilder.DropIndex(
                name: "UX_OpportunitySources_Opportunity_SourceUrl",
                table: "OpportunitySources");

            migrationBuilder.DropIndex(
                name: "UX_OpportunitySources_SourceConfiguration_ExternalId",
                table: "OpportunitySources");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_WorkspaceId_OpportunityId",
                table: "OpportunitySources",
                columns: new[] { "WorkspaceId", "OpportunityId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_WorkspaceId_SavedSearchId",
                table: "OpportunitySources",
                columns: new[] { "WorkspaceId", "SavedSearchId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_WorkspaceId_SourceConfigurationId",
                table: "OpportunitySources",
                columns: new[] { "WorkspaceId", "SourceConfigurationId" });

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_WorkspaceId_SourceExecutionId",
                table: "OpportunitySources",
                columns: new[] { "WorkspaceId", "SourceExecutionId" });

            migrationBuilder.CreateIndex(
                name: "UX_OpportunitySources_Workspace_NormalizedSourceUrl",
                table: "OpportunitySources",
                columns: new[] { "WorkspaceId", "NormalizedSourceUrl" },
                unique: true,
                filter: "\"NormalizedSourceUrl\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_OpportunitySources_Workspace_SourceConfiguration_ExternalId",
                table: "OpportunitySources",
                columns: new[] { "WorkspaceId", "SourceConfigurationId", "ExternalId" },
                unique: true,
                filter: "\"SourceConfigurationId\" IS NOT NULL AND \"ExternalId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OpportunitySources_ExternalConfiguration",
                table: "OpportunitySources",
                sql: "\"ExternalId\" IS NULL OR \"SourceConfigurationId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OpportunitySources_UrlPair",
                table: "OpportunitySources",
                sql: "(\"SourceUrl\" IS NULL) = (\"NormalizedSourceUrl\" IS NULL)");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OpportunitySources_Opportunities_WorkspaceId_OpportunityId",
                table: "OpportunitySources");

            migrationBuilder.DropForeignKey(
                name: "FK_OpportunitySources_SavedSearches_WorkspaceId_SavedSearchId",
                table: "OpportunitySources");

            migrationBuilder.DropForeignKey(
                name: "FK_OpportunitySources_SourceConfigurations_WorkspaceId_SourceC~",
                table: "OpportunitySources");

            migrationBuilder.DropForeignKey(
                name: "FK_OpportunitySources_SourceExecutions_WorkspaceId_SourceExecu~",
                table: "OpportunitySources");

            migrationBuilder.DropForeignKey(
                name: "FK_OpportunitySources_Workspaces_WorkspaceId",
                table: "OpportunitySources");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SourceConfigurations_WorkspaceId_Id",
                table: "SourceConfigurations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SavedSearches_WorkspaceId_Id",
                table: "SavedSearches");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_OpportunitySources_WorkspaceId_Id",
                table: "OpportunitySources");

            migrationBuilder.DropIndex(
                name: "IX_OpportunitySources_WorkspaceId_OpportunityId",
                table: "OpportunitySources");

            migrationBuilder.DropIndex(
                name: "IX_OpportunitySources_WorkspaceId_SavedSearchId",
                table: "OpportunitySources");

            migrationBuilder.DropIndex(
                name: "IX_OpportunitySources_WorkspaceId_SourceConfigurationId",
                table: "OpportunitySources");

            migrationBuilder.DropIndex(
                name: "IX_OpportunitySources_WorkspaceId_SourceExecutionId",
                table: "OpportunitySources");

            migrationBuilder.DropIndex(
                name: "UX_OpportunitySources_Workspace_NormalizedSourceUrl",
                table: "OpportunitySources");

            migrationBuilder.DropIndex(
                name: "UX_OpportunitySources_Workspace_SourceConfiguration_ExternalId",
                table: "OpportunitySources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OpportunitySources_ExternalConfiguration",
                table: "OpportunitySources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OpportunitySources_UrlPair",
                table: "OpportunitySources");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Opportunities_WorkspaceId_Id",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "NormalizedSourceUrl",
                table: "OpportunitySources");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "OpportunitySources");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_OpportunityId",
                table: "OpportunitySources",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_SavedSearchId",
                table: "OpportunitySources",
                column: "SavedSearchId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_SourceConfigurationId",
                table: "OpportunitySources",
                column: "SourceConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_OpportunitySources_SourceExecutionId",
                table: "OpportunitySources",
                column: "SourceExecutionId");

            migrationBuilder.CreateIndex(
                name: "UX_OpportunitySources_Opportunity_SourceUrl",
                table: "OpportunitySources",
                columns: new[] { "OpportunityId", "SourceUrl" },
                unique: true,
                filter: "\"SourceUrl\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_OpportunitySources_SourceConfiguration_ExternalId",
                table: "OpportunitySources",
                columns: new[] { "SourceConfigurationId", "ExternalId" },
                unique: true,
                filter: "\"SourceConfigurationId\" IS NOT NULL AND \"ExternalId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_OpportunitySources_Opportunities_OpportunityId",
                table: "OpportunitySources",
                column: "OpportunityId",
                principalTable: "Opportunities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_OpportunitySources_SavedSearches_SavedSearchId",
                table: "OpportunitySources",
                column: "SavedSearchId",
                principalTable: "SavedSearches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OpportunitySources_SourceConfigurations_SourceConfiguration~",
                table: "OpportunitySources",
                column: "SourceConfigurationId",
                principalTable: "SourceConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OpportunitySources_SourceExecutions_SourceExecutionId",
                table: "OpportunitySources",
                column: "SourceExecutionId",
                principalTable: "SourceExecutions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
