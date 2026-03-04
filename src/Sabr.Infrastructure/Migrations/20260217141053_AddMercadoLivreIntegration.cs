using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sabr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMercadoLivreIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "marketplace_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ml_order_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    status = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shipment_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    shipping_mode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    logistic_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ship_by_deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sabr_payment_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    risk_flags_json = table.Column<string>(type: "jsonb", nullable: true),
                    raw_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_marketplace_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    nickname = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    access_token = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    refresh_token = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_marketplace_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_marketplace_listing_maps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ml_item_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ml_variation_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    sabr_variant_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_marketplace_listing_maps", x => x.id);
                    table.CheckConstraint("ck_tenant_marketplace_listing_maps_sku_format", "\"sabr_variant_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
                });

            migrationBuilder.CreateTable(
                name: "marketplace_order_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    marketplace_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ml_item_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ml_variation_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    sabr_variant_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    reserved_quantity = table.Column<int>(type: "integer", nullable: false),
                    mapping_state = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    raw_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_order_items", x => x.id);
                    table.CheckConstraint("ck_marketplace_order_items_quantity_positive", "\"quantity\" > 0");
                    table.CheckConstraint("ck_marketplace_order_items_reserved_non_negative", "\"reserved_quantity\" >= 0");
                    table.CheckConstraint("ck_marketplace_order_items_sku_format", "\"sabr_variant_sku\" IS NULL OR \"sabr_variant_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
                    table.ForeignKey(
                        name: "FK_marketplace_order_items_marketplace_orders_marketplace_orde~",
                        column: x => x.marketplace_order_id,
                        principalTable: "marketplace_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sabr_variant_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    marketplace_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    marketplace_order_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_reservations", x => x.id);
                    table.CheckConstraint("ck_stock_reservations_quantity_positive", "\"quantity\" > 0");
                    table.CheckConstraint("ck_stock_reservations_sku_format", "\"sabr_variant_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
                    table.ForeignKey(
                        name: "FK_stock_reservations_marketplace_order_items_marketplace_orde~",
                        column: x => x.marketplace_order_item_id,
                        principalTable: "marketplace_order_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_stock_reservations_marketplace_orders_marketplace_order_id",
                        column: x => x.marketplace_order_id,
                        principalTable: "marketplace_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_stock_reservations_product_variants_sabr_variant_sku",
                        column: x => x.sabr_variant_sku,
                        principalTable: "product_variants",
                        principalColumn: "variant_sku",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_order_items_scope_mapping_state",
                table: "marketplace_order_items",
                columns: new[] { "tenant_id", "client_id", "mapping_state" });

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_order_items_order_item_variation",
                table: "marketplace_order_items",
                columns: new[] { "marketplace_order_id", "ml_item_id", "ml_variation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_orders_scope_status_imported",
                table: "marketplace_orders",
                columns: new[] { "tenant_id", "client_id", "provider", "status", "imported_at" });

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_orders_scope_provider_ml_order",
                table: "marketplace_orders",
                columns: new[] { "tenant_id", "client_id", "provider", "ml_order_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_reservations_marketplace_order_item_id",
                table: "stock_reservations",
                column: "marketplace_order_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_order_item",
                table: "stock_reservations",
                columns: new[] { "marketplace_order_id", "marketplace_order_item_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_reservations_sabr_variant_sku",
                table: "stock_reservations",
                column: "sabr_variant_sku");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_scope_sku_status_reserved_at",
                table: "stock_reservations",
                columns: new[] { "tenant_id", "client_id", "sabr_variant_sku", "status", "reserved_at" });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_marketplace_connections_scope_seller",
                table: "tenant_marketplace_connections",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_marketplace_listing_maps_scope_sku",
                table: "tenant_marketplace_listing_maps",
                columns: new[] { "tenant_id", "client_id", "sabr_variant_sku" });

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stock_reservations");

            migrationBuilder.DropTable(
                name: "tenant_marketplace_connections");

            migrationBuilder.DropTable(
                name: "tenant_marketplace_listing_maps");

            migrationBuilder.DropTable(
                name: "marketplace_order_items");

            migrationBuilder.DropTable(
                name: "marketplace_orders");
        }
    }
}
