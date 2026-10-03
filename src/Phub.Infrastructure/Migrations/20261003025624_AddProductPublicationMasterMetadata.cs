using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductPublicationMasterMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cest",
                table: "products",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fiscal_origin",
                table: "products",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "supplier_name",
                table: "products",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_cest_format",
                table: "products",
                sql: "\"cest\" IS NULL OR \"cest\" ~ '^[0-9]{7}$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_products_cest_format",
                table: "products");

            migrationBuilder.DropColumn(
                name: "cest",
                table: "products");

            migrationBuilder.DropColumn(
                name: "fiscal_origin",
                table: "products");

            migrationBuilder.DropColumn(
                name: "supplier_name",
                table: "products");
        }
    }
}
