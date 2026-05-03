using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoriesAndProductVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    icon = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    description = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                    table.CheckConstraint("ck_categories_slug_format", "\"slug\" ~ '^[a-z0-9][a-z0-9_/-]{0,119}$'");
                    table.ForeignKey(
                        name: "FK_categories_categories_parent_id",
                        column: x => x.parent_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_variants",
                columns: table => new
                {
                    variant_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    base_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    cost_price_cents = table.Column<long>(type: "bigint", nullable: false),
                    catalog_price_cents = table.Column<long>(type: "bigint", nullable: false),
                    physical_stock = table.Column<int>(type: "integer", nullable: false),
                    reserved_stock = table.Column<int>(type: "integer", nullable: false),
                    available_stock = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_variants", x => x.variant_sku);
                    table.CheckConstraint("ck_product_variants_available_consistency", "\"available_stock\" = \"physical_stock\" - \"reserved_stock\"");
                    table.CheckConstraint("ck_product_variants_available_non_negative", "\"available_stock\" >= 0");
                    table.CheckConstraint("ck_product_variants_base_sku_format", "\"base_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
                    table.CheckConstraint("ck_product_variants_catalog_non_negative", "\"catalog_price_cents\" >= 0");
                    table.CheckConstraint("ck_product_variants_cost_non_negative", "\"cost_price_cents\" >= 0");
                    table.CheckConstraint("ck_product_variants_physical_non_negative", "\"physical_stock\" >= 0");
                    table.CheckConstraint("ck_product_variants_reserved_non_negative", "\"reserved_stock\" >= 0");
                    table.CheckConstraint("ck_product_variants_variant_sku_format", "\"variant_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
                    table.ForeignKey(
                        name: "FK_product_variants_products_base_sku",
                        column: x => x.base_sku,
                        principalTable: "products",
                        principalColumn: "sku",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_categories_active",
                table: "categories",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_categories_parent",
                table: "categories",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ux_categories_slug",
                table: "categories",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_base_sku",
                table: "product_variants",
                column: "base_sku");

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_base_sku_active",
                table: "product_variants",
                columns: new[] { "base_sku", "is_active" });

            migrationBuilder.Sql("""
                INSERT INTO categories (id, name, slug, parent_id, icon, description, is_active, created_at, updated_at)
                VALUES ('55555555-5555-5555-5555-555555555555', 'Sem Categoria', 'uncategorized', NULL, NULL, 'Categoria padrao tecnica para compatibilidade legado.', TRUE, NOW(), NOW())
                ON CONFLICT (slug) DO UPDATE
                SET name = EXCLUDED.name,
                    is_active = TRUE,
                    updated_at = NOW();
                """);

            migrationBuilder.Sql("""
                UPDATE products
                SET category_id = 'uncategorized'
                WHERE category_id IS NULL OR btrim(category_id) = '';
                """);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM products p
                        JOIN product_variants pv ON pv.variant_sku = p.sku
                        WHERE pv.base_sku <> p.sku
                    ) THEN
                        RAISE EXCEPTION 'Backfill conflict: variant_sku already linked to different base_sku.';
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                INSERT INTO product_variants (
                    variant_sku,
                    base_sku,
                    name,
                    cost_price_cents,
                    catalog_price_cents,
                    physical_stock,
                    reserved_stock,
                    available_stock,
                    is_active,
                    created_at,
                    updated_at
                )
                SELECT
                    p.sku,
                    p.sku,
                    p.name,
                    p.cost_price_cents,
                    p.catalog_price_cents,
                    0,
                    0,
                    0,
                    p.is_active,
                    NOW(),
                    NOW()
                FROM products p
                ON CONFLICT (variant_sku) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "product_variants");
        }
    }
}
