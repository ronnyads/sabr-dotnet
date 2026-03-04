using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sabr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddListingDraftsPublishFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_tenant_marketplace_listing_maps_scope_item_no_var",
                table: "tenant_marketplace_listing_maps");

            migrationBuilder.DropIndex(
                name: "ux_tenant_marketplace_listing_maps_scope_item_var",
                table: "tenant_marketplace_listing_maps");

            migrationBuilder.AddColumn<Guid>(
                name: "integration_id",
                table: "tenant_marketplace_listing_maps",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE tenant_marketplace_listing_maps AS map
                SET integration_id = conn.id
                FROM (
                    SELECT
                        tenant_id,
                        client_id,
                        provider,
                        seller_id,
                        (ARRAY_AGG(id ORDER BY id))[1] AS id
                    FROM tenant_marketplace_connections
                    GROUP BY tenant_id, client_id, provider, seller_id
                    HAVING COUNT(*) = 1
                ) AS conn
                WHERE map.integration_id IS NULL
                  AND conn.tenant_id = map.tenant_id
                  AND conn.client_id = map.client_id
                  AND conn.provider = map.provider
                  AND conn.seller_id = map.seller_id;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE ambiguous_count integer;
                BEGIN
                  SELECT COUNT(*)
                  INTO ambiguous_count
                  FROM tenant_marketplace_listing_maps AS map
                  JOIN (
                    SELECT tenant_id, client_id, provider, seller_id
                    FROM tenant_marketplace_connections
                    GROUP BY tenant_id, client_id, provider, seller_id
                    HAVING COUNT(*) > 1
                  ) AS ambiguous
                    ON ambiguous.tenant_id = map.tenant_id
                   AND ambiguous.client_id = map.client_id
                   AND ambiguous.provider = map.provider
                   AND ambiguous.seller_id = map.seller_id
                  WHERE map.integration_id IS NULL;

                  IF ambiguous_count > 0 THEN
                    RAISE WARNING 'ML_BACKFILL_INTEGRATION_AMBIGUOUS count=%', ambiguous_count;
                  END IF;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "listing_drafts",
                columns: table => new
                {
                    draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    base_product_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sabr_variant_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    category_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    listing_type_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    price_cents = table.Column<long>(type: "bigint", nullable: true),
                    currency_id = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    provider_draft_json = table.Column<string>(type: "jsonb", nullable: false),
                    published_item_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    published_variation_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    published_permalink = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    published_api_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    last_error_raw_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listing_drafts", x => x.draft_id);
                    table.CheckConstraint("ck_listing_drafts_base_sku_format", "\"base_product_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
                    table.CheckConstraint("ck_listing_drafts_price_non_negative", "\"price_cents\" IS NULL OR \"price_cents\" >= 0");
                    table.CheckConstraint("ck_listing_drafts_variant_sku_format", "\"sabr_variant_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_marketplace_listing_maps_scope_integration_sku",
                table: "tenant_marketplace_listing_maps",
                columns: new[] { "tenant_id", "client_id", "provider", "integration_id", "sabr_variant_sku" });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_marketplace_listing_maps_scope_item_no_var",
                table: "tenant_marketplace_listing_maps",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "integration_id", "ml_item_id" },
                unique: true,
                filter: "\"ml_variation_id\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_marketplace_listing_maps_scope_item_var",
                table: "tenant_marketplace_listing_maps",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "integration_id", "ml_item_id", "ml_variation_id" },
                unique: true,
                filter: "\"ml_variation_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_listing_drafts_scope_base_sku",
                table: "listing_drafts",
                columns: new[] { "tenant_id", "client_id", "provider", "base_product_sku" });

            migrationBuilder.CreateIndex(
                name: "ix_listing_drafts_scope_status_updated",
                table: "listing_drafts",
                columns: new[] { "tenant_id", "client_id", "provider", "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ux_listing_drafts_scope_integration_variant",
                table: "listing_drafts",
                columns: new[] { "tenant_id", "client_id", "provider", "integration_id", "sabr_variant_sku" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "listing_drafts");

            migrationBuilder.DropIndex(
                name: "ix_tenant_marketplace_listing_maps_scope_integration_sku",
                table: "tenant_marketplace_listing_maps");

            migrationBuilder.DropIndex(
                name: "ux_tenant_marketplace_listing_maps_scope_item_no_var",
                table: "tenant_marketplace_listing_maps");

            migrationBuilder.DropIndex(
                name: "ux_tenant_marketplace_listing_maps_scope_item_var",
                table: "tenant_marketplace_listing_maps");

            migrationBuilder.DropColumn(
                name: "integration_id",
                table: "tenant_marketplace_listing_maps");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_marketplace_listing_maps_scope_item_no_var",
                table: "tenant_marketplace_listing_maps",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "ml_item_id" },
                unique: true,
                filter: "\"ml_variation_id\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_marketplace_listing_maps_scope_item_var",
                table: "tenant_marketplace_listing_maps",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "ml_item_id", "ml_variation_id" },
                unique: true,
                filter: "\"ml_variation_id\" IS NOT NULL");
        }
    }
}
