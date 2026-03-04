using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sabr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductMarketplaceFieldsAndImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "anatel_document_id",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "anatel_homologation_number",
                table: "products",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "brand",
                table: "products",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "category_id",
                table: "products",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "products",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ean",
                table: "products",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "height_cm",
                table: "products",
                type: "numeric(10,3)",
                precision: 10,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "length_cm",
                table: "products",
                type: "numeric(10,3)",
                precision: 10,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ncm",
                table: "products",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "requires_anatel",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "weight_kg",
                table: "products",
                type: "numeric(10,3)",
                precision: 10,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "width_cm",
                table: "products",
                type: "numeric(10,3)",
                precision: 10,
                scale: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "product_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_images", x => x.id);
                    table.CheckConstraint("ck_product_images_size_non_negative", "\"size_bytes\" >= 0");
                    table.CheckConstraint("ck_product_images_sku_format", "\"product_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
                    table.CheckConstraint("ck_product_images_sort_non_negative", "\"sort_order\" >= 0");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_height_non_negative",
                table: "products",
                sql: "\"height_cm\" IS NULL OR \"height_cm\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_length_non_negative",
                table: "products",
                sql: "\"length_cm\" IS NULL OR \"length_cm\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_weight_non_negative",
                table: "products",
                sql: "\"weight_kg\" IS NULL OR \"weight_kg\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_width_non_negative",
                table: "products",
                sql: "\"width_cm\" IS NULL OR \"width_cm\" >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_product_images_sku_primary",
                table: "product_images",
                columns: new[] { "product_sku", "is_primary" });

            migrationBuilder.CreateIndex(
                name: "ix_product_images_sku_sort",
                table: "product_images",
                columns: new[] { "product_sku", "sort_order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_images");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_height_non_negative",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_length_non_negative",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_weight_non_negative",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_width_non_negative",
                table: "products");

            migrationBuilder.DropColumn(
                name: "anatel_document_id",
                table: "products");

            migrationBuilder.DropColumn(
                name: "anatel_homologation_number",
                table: "products");

            migrationBuilder.DropColumn(
                name: "brand",
                table: "products");

            migrationBuilder.DropColumn(
                name: "category_id",
                table: "products");

            migrationBuilder.DropColumn(
                name: "description",
                table: "products");

            migrationBuilder.DropColumn(
                name: "ean",
                table: "products");

            migrationBuilder.DropColumn(
                name: "height_cm",
                table: "products");

            migrationBuilder.DropColumn(
                name: "length_cm",
                table: "products");

            migrationBuilder.DropColumn(
                name: "ncm",
                table: "products");

            migrationBuilder.DropColumn(
                name: "requires_anatel",
                table: "products");

            migrationBuilder.DropColumn(
                name: "weight_kg",
                table: "products");

            migrationBuilder.DropColumn(
                name: "width_cm",
                table: "products");
        }
    }
}
