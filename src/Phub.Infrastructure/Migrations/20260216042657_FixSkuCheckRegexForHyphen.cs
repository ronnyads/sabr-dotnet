using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixSkuCheckRegexForHyphen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_publications_sku_format",
                table: "publications");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_sku_format",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_price_history_sku_format",
                table: "product_price_history");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_catalogs_sku_format",
                table: "product_catalogs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_publications_sku_format",
                table: "publications",
                sql: "\"product_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_sku_format",
                table: "products",
                sql: "\"sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_price_history_sku_format",
                table: "product_price_history",
                sql: "\"product_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_catalogs_sku_format",
                table: "product_catalogs",
                sql: "\"product_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_publications_sku_format",
                table: "publications");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_sku_format",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_price_history_sku_format",
                table: "product_price_history");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_catalogs_sku_format",
                table: "product_catalogs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_publications_sku_format",
                table: "publications",
                sql: "\"product_sku\" ~ '^[A-Z0-9][A-Z0-9\\\\-_/]{0,63}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_sku_format",
                table: "products",
                sql: "\"sku\" ~ '^[A-Z0-9][A-Z0-9\\\\-_/]{0,63}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_price_history_sku_format",
                table: "product_price_history",
                sql: "\"product_sku\" ~ '^[A-Z0-9][A-Z0-9\\\\-_/]{0,63}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_catalogs_sku_format",
                table: "product_catalogs",
                sql: "\"product_sku\" ~ '^[A-Z0-9][A-Z0-9\\\\-_/]{0,63}$'");
        }
    }
}
