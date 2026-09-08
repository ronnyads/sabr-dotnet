using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStockJobDedupeKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "dedupe_key",
                table: "marketplace_operation_jobs",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_operation_jobs_dedupe_key",
                table: "marketplace_operation_jobs",
                column: "dedupe_key",
                unique: true,
                filter: "\"dedupe_key\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_marketplace_operation_jobs_dedupe_key",
                table: "marketplace_operation_jobs");

            migrationBuilder.DropColumn(
                name: "dedupe_key",
                table: "marketplace_operation_jobs");
        }
    }
}
