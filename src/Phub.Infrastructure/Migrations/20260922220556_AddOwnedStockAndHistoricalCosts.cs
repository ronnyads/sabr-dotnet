using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnedStockAndHistoricalCosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_product_variants_available_consistency",
                table: "product_variants");

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "stock_reservations",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "GENERAL_STOCK");

            migrationBuilder.AddColumn<int>(
                name: "client_owned_stock",
                table: "product_variants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "pricing_mode",
                table: "product_variants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "INHERITED");

            migrationBuilder.AddColumn<Guid>(
                name: "catalog_price_version_id",
                table: "marketplace_order_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_references_json",
                table: "marketplace_order_items",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "cost_source",
                table: "marketplace_order_items",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "economic_at",
                table: "marketplace_order_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "economic_at_source",
                table: "marketplace_order_items",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "financial_correction_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    plan_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scope_json = table.Column<string>(type: "jsonb", nullable: false),
                    report_json = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    total_entries = table.Column<int>(type: "integer", nullable: false),
                    processed_entries = table.Column<int>(type: "integer", nullable: false),
                    last_processed_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_financial_correction_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_price_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    variant_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    pricing_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    cost_price_cents = table.Column<long>(type: "bigint", nullable: false),
                    catalog_price_cents = table.Column<long>(type: "bigint", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    change_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_price_versions", x => x.id);
                    table.CheckConstraint("ck_product_price_version_amounts", "cost_price_cents >= 0 AND catalog_price_cents >= 0");
                    table.CheckConstraint("ck_product_price_version_change", "change_type IN ('CHANGE','CORRECTION')");
                    table.CheckConstraint("ck_product_price_version_mode", "pricing_mode IN ('INHERITED','OVERRIDE')");
                    table.CheckConstraint("ck_product_price_version_range", "valid_to IS NULL OR valid_to > valid_from");
                });

            migrationBuilder.CreateTable(
                name: "seller_owned_stock_lots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    variant_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    original_quantity = table.Column<int>(type: "integer", nullable: false),
                    available_quantity = table.Column<int>(type: "integer", nullable: false),
                    reserved_quantity = table.Column<int>(type: "integer", nullable: false),
                    consumed_quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_cost_cents = table.Column<long>(type: "bigint", nullable: false),
                    currency_id = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    source_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    evidence_reference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    acquired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seller_owned_stock_lots", x => x.id);
                    table.CheckConstraint("ck_seller_owned_stock_lot_cost", "unit_cost_cents > 0");
                    table.CheckConstraint("ck_seller_owned_stock_lot_quantities", "original_quantity > 0 AND available_quantity >= 0 AND reserved_quantity >= 0 AND consumed_quantity >= 0 AND available_quantity + reserved_quantity + consumed_quantity = original_quantity");
                    table.ForeignKey(
                        name: "FK_seller_owned_stock_lots_product_variants_variant_sku",
                        column: x => x.variant_sku,
                        principalTable: "product_variants",
                        principalColumn: "variant_sku",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_correction_plan_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replacement_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    marketplace_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    economic_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_financial_correction_plan_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_financial_correction_plan_entries_financial_correction_plan~",
                        column: x => x.plan_id,
                        principalTable: "financial_correction_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_financial_correction_plan_entries_marketplace_financial_ent~",
                        column: x => x.original_entry_id,
                        principalTable: "marketplace_financial_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_financial_correction_plan_entries_marketplace_financial_en~1",
                        column: x => x.replacement_entry_id,
                        principalTable: "marketplace_financial_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_reservation_allocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_owned_stock_lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_cost_cents = table.Column<long>(type: "bigint", nullable: true),
                    currency_id = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_reservation_allocations", x => x.id);
                    table.CheckConstraint("ck_stock_reservation_allocation_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_stock_reservation_allocations_seller_owned_stock_lots_selle~",
                        column: x => x.seller_owned_stock_lot_id,
                        principalTable: "seller_owned_stock_lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_reservation_allocations_stock_reservations_stock_rese~",
                        column: x => x.stock_reservation_id,
                        principalTable: "stock_reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql("""
                ALTER TABLE product_price_versions
                ADD CONSTRAINT ex_product_price_versions_no_overlap
                EXCLUDE USING gist (
                    product_sku WITH =,
                    (COALESCE(variant_sku, '')) WITH =,
                    tstzrange(valid_from, valid_to, '[)') WITH &&
                );
                """);

            migrationBuilder.Sql("""
                INSERT INTO product_price_versions
                    (id, product_sku, variant_sku, pricing_mode, cost_price_cents, catalog_price_cents,
                     valid_from, valid_to, version, change_type, changed_by_user_id, reason, created_at)
                SELECT gen_random_uuid(), p.sku, NULL, 'OVERRIDE', p.cost_price_cents, p.catalog_price_cents,
                       p.created_at, NULL, 1, 'CHANGE', '00000000-0000-0000-0000-000000000000',
                       'Versão inicial migrada', NOW()
                FROM products p;

                INSERT INTO product_price_versions
                    (id, product_sku, variant_sku, pricing_mode, cost_price_cents, catalog_price_cents,
                     valid_from, valid_to, version, change_type, changed_by_user_id, reason, created_at)
                SELECT gen_random_uuid(), v.base_sku, v.variant_sku,
                       CASE WHEN v.cost_price_cents = p.cost_price_cents AND v.catalog_price_cents = p.catalog_price_cents
                            THEN 'INHERITED' ELSE 'OVERRIDE' END,
                       v.cost_price_cents, v.catalog_price_cents, v.created_at, NULL, 1, 'CHANGE',
                       '00000000-0000-0000-0000-000000000000', 'Versão inicial migrada', NOW()
                FROM product_variants v JOIN products p ON p.sku = v.base_sku;

                UPDATE product_variants v SET pricing_mode = CASE
                    WHEN v.cost_price_cents = p.cost_price_cents AND v.catalog_price_cents = p.catalog_price_cents
                    THEN 'INHERITED' ELSE 'OVERRIDE' END
                FROM products p WHERE p.sku = v.base_sku;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_variants_available_consistency",
                table: "product_variants",
                sql: "\"available_stock\" = GREATEST(0, \"physical_stock\" - \"client_owned_stock\" - \"reserved_stock\" - \"safety_buffer\")");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_variants_client_owned_non_negative",
                table: "product_variants",
                sql: "\"client_owned_stock\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_variants_owned_not_above_physical",
                table: "product_variants",
                sql: "\"client_owned_stock\" <= \"physical_stock\"");

            migrationBuilder.CreateIndex(
                name: "IX_financial_correction_plan_entries_original_entry_id",
                table: "financial_correction_plan_entries",
                column: "original_entry_id");

            migrationBuilder.CreateIndex(
                name: "ux_financial_correction_plan_entries_original",
                table: "financial_correction_plan_entries",
                columns: new[] { "plan_id", "original_entry_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_financial_correction_plan_entries_replacement",
                table: "financial_correction_plan_entries",
                column: "replacement_entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_correction_plans_status_updated",
                table: "financial_correction_plans",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ux_financial_correction_plans_hash",
                table: "financial_correction_plans",
                columns: new[] { "tenant_id", "client_id", "seller_id", "plan_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_price_versions_scope_valid_from",
                table: "product_price_versions",
                columns: new[] { "product_sku", "variant_sku", "valid_from" });

            migrationBuilder.CreateIndex(
                name: "ux_product_price_versions_scope_version",
                table: "product_price_versions",
                columns: new[] { "product_sku", "variant_sku", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seller_owned_stock_lots_fifo",
                table: "seller_owned_stock_lots",
                columns: new[] { "tenant_id", "client_id", "seller_id", "variant_sku", "acquired_at" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_owned_stock_lots_variant_sku",
                table: "seller_owned_stock_lots",
                column: "variant_sku");

            migrationBuilder.CreateIndex(
                name: "ux_seller_owned_stock_lots_source",
                table: "seller_owned_stock_lots",
                columns: new[] { "tenant_id", "client_id", "seller_id", "source_type", "source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_reservation_allocations_seller_owned_stock_lot_id",
                table: "stock_reservation_allocations",
                column: "seller_owned_stock_lot_id");

            migrationBuilder.CreateIndex(
                name: "ux_stock_reservation_allocations_origin",
                table: "stock_reservation_allocations",
                columns: new[] { "stock_reservation_id", "seller_owned_stock_lot_id", "source" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE product_price_versions DROP CONSTRAINT IF EXISTS ex_product_price_versions_no_overlap;");
            migrationBuilder.DropTable(
                name: "financial_correction_plan_entries");

            migrationBuilder.DropTable(
                name: "product_price_versions");

            migrationBuilder.DropTable(
                name: "stock_reservation_allocations");

            migrationBuilder.DropTable(
                name: "financial_correction_plans");

            migrationBuilder.DropTable(
                name: "seller_owned_stock_lots");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_variants_available_consistency",
                table: "product_variants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_variants_client_owned_non_negative",
                table: "product_variants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_variants_owned_not_above_physical",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "source",
                table: "stock_reservations");

            migrationBuilder.DropColumn(
                name: "client_owned_stock",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "pricing_mode",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "catalog_price_version_id",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "cost_references_json",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "cost_source",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "economic_at",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "economic_at_source",
                table: "marketplace_order_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_variants_available_consistency",
                table: "product_variants",
                sql: "\"available_stock\" = GREATEST(0, \"physical_stock\" - \"reserved_stock\" - \"safety_buffer\")");
        }
    }
}
