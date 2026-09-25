using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditableProfitabilityCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_financial_correction_plan_entries_replacement",
                table: "financial_correction_plan_entries");

            migrationBuilder.AddColumn<string>(
                name: "catalog_cost_status",
                table: "products",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "RESOLVED");

            migrationBuilder.AddColumn<string>(
                name: "catalog_price_origin",
                table: "products",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "MASTER_PRODUCT");

            migrationBuilder.AddColumn<string>(
                name: "catalog_cost_status",
                table: "product_variants",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "RESOLVED");

            migrationBuilder.AddColumn<string>(
                name: "catalog_price_origin",
                table: "product_variants",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "MASTER_PRODUCT");

            migrationBuilder.AddColumn<string>(
                name: "catalog_cost_status",
                table: "product_price_versions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "RESOLVED");

            migrationBuilder.AddColumn<string>(
                name: "catalog_price_origin",
                table: "product_price_versions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "MASTER_PRODUCT");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cost_accrued_at",
                table: "marketplace_order_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cost_settled_at",
                table: "marketplace_order_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "internal_cost_status",
                table: "marketplace_order_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NONE");

            migrationBuilder.AddColumn<Guid>(
                name: "internal_wallet_entry_id",
                table: "marketplace_order_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "product_cost_entry_id",
                table: "marketplace_order_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "replacement_entry_id",
                table: "financial_correction_plan_entries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<long>(
                name: "current_amount_cents",
                table: "financial_correction_plan_entries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "expected_active_entry_hash",
                table: "financial_correction_plan_entries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "expected_active_entry_id",
                table: "financial_correction_plan_entries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "expected_cost_references_hash",
                table: "financial_correction_plan_entries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE products
                SET catalog_cost_status = CASE WHEN catalog_price_cents > 0 THEN 'RESOLVED' ELSE 'CATALOG_COST_PENDING' END,
                    catalog_price_origin = CASE WHEN catalog_price_cents > 0 THEN 'MASTER_PRODUCT' ELSE 'NONE' END;
                UPDATE product_variants
                SET catalog_cost_status = CASE WHEN catalog_price_cents > 0 THEN 'RESOLVED' ELSE 'CATALOG_COST_PENDING' END,
                    catalog_price_origin = CASE
                        WHEN catalog_price_cents <= 0 THEN 'NONE'
                        WHEN pricing_mode = 'OVERRIDE' THEN 'VARIANT_OVERRIDE'
                        ELSE 'MASTER_PRODUCT' END;
                UPDATE product_price_versions
                SET catalog_cost_status = CASE WHEN catalog_price_cents > 0 THEN 'RESOLVED' ELSE 'CATALOG_COST_PENDING' END,
                    catalog_price_origin = CASE
                        WHEN catalog_price_cents <= 0 THEN 'NONE'
                        WHEN pricing_mode = 'OVERRIDE' THEN 'VARIANT_OVERRIDE'
                        ELSE 'MASTER_PRODUCT' END;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "expected_head_id",
                table: "financial_correction_plan_entries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "expected_head_version",
                table: "financial_correction_plan_entries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "expected_price_version_id",
                table: "financial_correction_plan_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "replacement_amount_cents",
                table: "financial_correction_plan_entries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "replacement_breakdown_json",
                table: "financial_correction_plan_entries",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "financial_correction_plan_entries",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "ACTIVE");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_catalog_cost_state",
                table: "products",
                sql: "(\"catalog_cost_status\" = 'CATALOG_COST_PENDING' AND \"catalog_price_cents\" = 0 AND \"catalog_price_origin\" = 'NONE') OR (\"catalog_cost_status\" = 'RESOLVED' AND \"catalog_price_cents\" > 0 AND \"catalog_price_origin\" IN ('MASTER_PRODUCT','VARIANT_OVERRIDE','EXTERNAL_SUPPLIER','PREPURCHASED_LOT'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_variants_catalog_cost_state",
                table: "product_variants",
                sql: "(\"catalog_cost_status\" = 'CATALOG_COST_PENDING' AND \"catalog_price_cents\" = 0 AND \"catalog_price_origin\" = 'NONE') OR (\"catalog_cost_status\" = 'RESOLVED' AND \"catalog_price_cents\" > 0 AND \"catalog_price_origin\" IN ('MASTER_PRODUCT','VARIANT_OVERRIDE','EXTERNAL_SUPPLIER','PREPURCHASED_LOT'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_price_version_catalog_cost_state",
                table: "product_price_versions",
                sql: "(catalog_cost_status = 'CATALOG_COST_PENDING' AND catalog_price_cents = 0 AND catalog_price_origin = 'NONE') OR (catalog_cost_status = 'RESOLVED' AND catalog_price_cents > 0 AND catalog_price_origin IN ('MASTER_PRODUCT','VARIANT_OVERRIDE','EXTERNAL_SUPPLIER','PREPURCHASED_LOT'))");

            migrationBuilder.CreateIndex(
                name: "ux_financial_correction_plan_entries_replacement",
                table: "financial_correction_plan_entries",
                column: "replacement_entry_id",
                unique: true,
                filter: "\"replacement_entry_id\" IS NOT NULL");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION reject_overlapping_product_price_versions()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM product_price_versions existing
                        WHERE existing.id <> NEW.id
                          AND existing.product_sku = NEW.product_sku
                          AND COALESCE(existing.variant_sku, '') = COALESCE(NEW.variant_sku, '')
                          AND tstzrange(existing.valid_from, COALESCE(existing.valid_to, 'infinity'::timestamptz), '[)')
                              && tstzrange(NEW.valid_from, COALESCE(NEW.valid_to, 'infinity'::timestamptz), '[)')
                    ) THEN
                        RAISE EXCEPTION 'Product price version validity ranges cannot overlap';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER trg_product_price_versions_no_overlap
                BEFORE INSERT OR UPDATE OF product_sku, variant_sku, valid_from, valid_to
                ON product_price_versions
                FOR EACH ROW EXECUTE FUNCTION reject_overlapping_product_price_versions();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_product_price_versions_no_overlap ON product_price_versions;
                DROP FUNCTION IF EXISTS reject_overlapping_product_price_versions();
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_products_catalog_cost_state",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_variants_catalog_cost_state",
                table: "product_variants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_price_version_catalog_cost_state",
                table: "product_price_versions");

            migrationBuilder.DropIndex(
                name: "ux_financial_correction_plan_entries_replacement",
                table: "financial_correction_plan_entries");

            migrationBuilder.DropColumn(
                name: "catalog_cost_status",
                table: "products");

            migrationBuilder.DropColumn(
                name: "catalog_price_origin",
                table: "products");

            migrationBuilder.DropColumn(
                name: "catalog_cost_status",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "catalog_price_origin",
                table: "product_variants");

            migrationBuilder.DropColumn(
                name: "catalog_cost_status",
                table: "product_price_versions");

            migrationBuilder.DropColumn(
                name: "catalog_price_origin",
                table: "product_price_versions");

            migrationBuilder.DropColumn(
                name: "cost_accrued_at",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "cost_settled_at",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "internal_cost_status",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "internal_wallet_entry_id",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "product_cost_entry_id",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "current_amount_cents",
                table: "financial_correction_plan_entries");

            migrationBuilder.DropColumn(
                name: "expected_active_entry_hash",
                table: "financial_correction_plan_entries");

            migrationBuilder.DropColumn(
                name: "expected_active_entry_id",
                table: "financial_correction_plan_entries");

            migrationBuilder.DropColumn(
                name: "expected_cost_references_hash",
                table: "financial_correction_plan_entries");

            migrationBuilder.DropColumn(
                name: "expected_head_id",
                table: "financial_correction_plan_entries");

            migrationBuilder.DropColumn(
                name: "expected_head_version",
                table: "financial_correction_plan_entries");

            migrationBuilder.DropColumn(
                name: "expected_price_version_id",
                table: "financial_correction_plan_entries");

            migrationBuilder.DropColumn(
                name: "replacement_amount_cents",
                table: "financial_correction_plan_entries");

            migrationBuilder.DropColumn(
                name: "replacement_breakdown_json",
                table: "financial_correction_plan_entries");

            migrationBuilder.DropColumn(
                name: "state",
                table: "financial_correction_plan_entries");

            migrationBuilder.AlterColumn<Guid>(
                name: "replacement_entry_id",
                table: "financial_correction_plan_entries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_financial_correction_plan_entries_replacement",
                table: "financial_correction_plan_entries",
                column: "replacement_entry_id",
                unique: true);
        }
    }
}
