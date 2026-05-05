using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddListingDraftWarrantyAndShipping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FreeShipping",
                table: "listing_drafts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WarrantyTime",
                table: "listing_drafts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WarrantyType",
                table: "listing_drafts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FreeShipping",
                table: "listing_drafts");

            migrationBuilder.DropColumn(
                name: "WarrantyTime",
                table: "listing_drafts");

            migrationBuilder.DropColumn(
                name: "WarrantyType",
                table: "listing_drafts");
        }
    }
}
