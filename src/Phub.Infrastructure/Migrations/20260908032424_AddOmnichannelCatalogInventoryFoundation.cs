using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOmnichannelCatalogInventoryFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_product_variants_available_consistency",
                table: "product_variants");

            migrationBuilder.AddColumn<string>(
                name: "channel_sku",
                table: "tenant_marketplace_listing_maps",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "mapping_version",
                table: "tenant_marketplace_listing_maps",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "user_product_id",
                table: "tenant_marketplace_listing_maps",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "inventory_version",
                table: "product_variants",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<int>(
                name: "safety_buffer",
                table: "product_variants",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<string>(
                name: "mapping_resolution_reason",
                table: "marketplace_order_items",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "mapping_resolved_at",
                table: "marketplace_order_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "mapping_snapshot_id",
                table: "marketplace_order_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "mapping_snapshot_version",
                table: "marketplace_order_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "access_mode",
                table: "catalogs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.Sql("""
                UPDATE product_variants
                SET available_stock = GREATEST(0, physical_stock - reserved_stock - safety_buffer),
                    inventory_version = GREATEST(1, inventory_version);

                INSERT INTO catalogs (id, name, description, access_mode, is_active, created_at, updated_at)
                VALUES (gen_random_uuid(), 'Catálogo Público', 'Produtos disponíveis para todos os clientes aprovados.', 0, TRUE, NOW(), NOW())
                ON CONFLICT (name) DO UPDATE
                SET access_mode = 0, is_active = TRUE, updated_at = NOW();

                INSERT INTO product_catalogs (id, catalog_id, product_sku, created_at)
                SELECT gen_random_uuid(), public_catalog.id, product.sku, NOW()
                FROM products product
                CROSS JOIN LATERAL (
                    SELECT id FROM catalogs WHERE name = 'Catálogo Público' LIMIT 1
                ) public_catalog
                WHERE product.is_active = TRUE
                ON CONFLICT (catalog_id, product_sku) DO NOTHING;

                UPDATE marketplace_order_items item
                SET mapping_snapshot_id = mapping.id,
                    mapping_snapshot_version = mapping.mapping_version,
                    mapping_resolution_reason = item.mapping_state,
                    mapping_resolved_at = COALESCE(item.created_at, NOW())
                FROM tenant_marketplace_listing_maps mapping
                WHERE item.mapping_resolved_at IS NULL
                  AND item.tenant_id = mapping.tenant_id
                  AND item.client_id = mapping.client_id
                  AND item.provider = mapping.provider
                  AND item.seller_id = mapping.seller_id
                  AND item.ml_item_id = mapping.ml_item_id
                  AND item.ml_variation_id IS NOT DISTINCT FROM mapping.ml_variation_id
                  AND item.sabr_variant_sku = mapping.sabr_variant_sku;

                UPDATE marketplace_order_items
                SET mapping_resolution_reason = mapping_state,
                    mapping_resolved_at = COALESCE(created_at, NOW())
                WHERE mapping_resolved_at IS NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_marketplace_listing_maps_version_positive",
                table: "tenant_marketplace_listing_maps",
                sql: "\"mapping_version\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_variants_available_consistency",
                table: "product_variants",
                sql: "\"available_stock\" = GREATEST(0, \"physical_stock\" - \"reserved_stock\" - \"safety_buffer\")");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_variants_buffer_non_negative",
                table: "product_variants",
                sql: "\"safety_buffer\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_variants_inventory_version_positive",
                table: "product_variants",
                sql: "\"inventory_version\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_marketplace_listing_maps_version_positive",
                table: "tenant_marketplace_listing_maps");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_variants_available_consistency",
                table: "product_variants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_variants_buffer_non_negative",
                table: "product_variants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_variants_inventory_version_positive",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "channel_sku",
                table: "tenant_marketplace_listing_maps");

            migrationBuilder.DropColumn(
                name: "mapping_version",
                table: "tenant_marketplace_listing_maps");

            migrationBuilder.DropColumn(
                name: "user_product_id",
                table: "tenant_marketplace_listing_maps");

            migrationBuilder.DropColumn(
                name: "inventory_version",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "safety_buffer",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "mapping_resolution_reason",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "mapping_resolved_at",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "mapping_snapshot_id",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "mapping_snapshot_version",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "access_mode",
                table: "catalogs");

            migrationBuilder.Sql("""
                UPDATE product_variants
                SET available_stock = physical_stock - reserved_stock;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_variants_available_consistency",
                table: "product_variants",
                sql: "\"available_stock\" = \"physical_stock\" - \"reserved_stock\"");
        }
    }
}
