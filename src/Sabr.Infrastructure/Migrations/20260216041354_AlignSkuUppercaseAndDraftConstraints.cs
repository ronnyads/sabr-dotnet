using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sabr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AlignSkuUppercaseAndDraftConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "product_sku",
                table: "publications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);

            migrationBuilder.AlterColumn<string>(
                name: "sku",
                table: "products",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);

            migrationBuilder.AlterColumn<string>(
                name: "product_sku",
                table: "product_price_history",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);

            migrationBuilder.AlterColumn<string>(
                name: "product_sku",
                table: "product_catalogs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);

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

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"ix_publications_tenant_client_sku\" ON publications (tenant_id, client_id, product_sku);");
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

            migrationBuilder.Sql("DROP INDEX IF EXISTS \"ix_publications_tenant_client_sku\";");

            migrationBuilder.AlterColumn<string>(
                name: "product_sku",
                table: "publications",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "sku",
                table: "products",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "product_sku",
                table: "product_price_history",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "product_sku",
                table: "product_catalogs",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);
        }
    }
}
