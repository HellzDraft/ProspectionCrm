using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProspectionCrm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase72CollectionWorkerLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "SourceCollectionJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseToken",
                table: "SourceCollectionJobs",
                type: "uuid",
                nullable: true);

            // Preserve legacy running rows without making them eligible for another attempt.
            migrationBuilder.Sql("""
                UPDATE "SourceCollectionJobs"
                SET "LeaseToken" = gen_random_uuid(), "LeaseExpiresAt" = "StartedAt" + interval '1 microsecond'
                WHERE "StatusCode" = 'running'
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceCollectionJobs_Lease",
                table: "SourceCollectionJobs",
                sql: "(\"StatusCode\" = 'running' AND \"LeaseToken\" IS NOT NULL\n    AND \"LeaseToken\" <> '00000000-0000-0000-0000-000000000000'::uuid\n    AND \"LeaseExpiresAt\" IS NOT NULL AND \"LeaseExpiresAt\" > \"StartedAt\")\nOR (\"StatusCode\" <> 'running' AND \"LeaseToken\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceCollectionJobs_Lease",
                table: "SourceCollectionJobs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "SourceCollectionJobs");

            migrationBuilder.DropColumn(
                name: "LeaseToken",
                table: "SourceCollectionJobs");
        }
    }
}
