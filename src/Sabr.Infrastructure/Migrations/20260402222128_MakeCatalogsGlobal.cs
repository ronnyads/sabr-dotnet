using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sabr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeCatalogsGlobal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_product_catalogs_tenant_sku",
                table: "product_catalogs");

            migrationBuilder.DropIndex(
                name: "ux_product_catalogs_tenant_catalog_sku",
                table: "product_catalogs");

            migrationBuilder.DropIndex(
                name: "ix_catalogs_tenant_active",
                table: "catalogs");

            migrationBuilder.DropIndex(
                name: "ux_catalogs_tenant_name",
                table: "catalogs");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "product_catalogs");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "catalogs");

            migrationBuilder.CreateIndex(
                name: "ix_product_catalogs_sku",
                table: "product_catalogs",
                column: "product_sku");

            migrationBuilder.CreateIndex(
                name: "ux_product_catalogs_catalog_sku",
                table: "product_catalogs",
                columns: new[] { "catalog_id", "product_sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_catalogs_active",
                table: "catalogs",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ux_catalogs_name",
                table: "catalogs",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_product_catalogs_sku",
                table: "product_catalogs");

            migrationBuilder.DropIndex(
                name: "ux_product_catalogs_catalog_sku",
                table: "product_catalogs");

            migrationBuilder.DropIndex(
                name: "ix_catalogs_active",
                table: "catalogs");

            migrationBuilder.DropIndex(
                name: "ux_catalogs_name",
                table: "catalogs");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                table: "product_catalogs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                table: "catalogs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_product_catalogs_tenant_sku",
                table: "product_catalogs",
                columns: new[] { "tenant_id", "product_sku" });

            migrationBuilder.CreateIndex(
                name: "ux_product_catalogs_tenant_catalog_sku",
                table: "product_catalogs",
                columns: new[] { "tenant_id", "catalog_id", "product_sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_catalogs_tenant_active",
                table: "catalogs",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_catalogs_tenant_name",
                table: "catalogs",
                columns: new[] { "tenant_id", "name" },
                unique: true);
        }
    }
}
