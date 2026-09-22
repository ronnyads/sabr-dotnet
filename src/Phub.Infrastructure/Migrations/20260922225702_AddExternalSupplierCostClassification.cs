using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalSupplierCostClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_cost_currency_id",
                table: "marketplace_order_items",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "external_cost_version_id",
                table: "marketplace_order_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_supplier_name",
                table: "marketplace_order_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "external_unit_cost_cents_snapshot",
                table: "marketplace_order_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "marketplace_listing_classification_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    integration_id = table.Column<Guid>(type: "uuid", nullable: true),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    external_item_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    external_variation_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    classification = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    supplier_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    external_unit_cost_cents = table.Column<long>(type: "bigint", nullable: true),
                    currency_id = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_listing_classification_versions", x => x.id);
                    table.CheckConstraint("ck_marketplace_listing_classification", "classification IN ('EXTERNAL_SUPPLIER','PENDING')");
                    table.CheckConstraint("ck_marketplace_listing_classification_version", "version > 0");
                    table.CheckConstraint("ck_marketplace_listing_external_cost", "external_unit_cost_cents IS NULL OR external_unit_cost_cents >= 0");
                    table.CheckConstraint("ck_marketplace_listing_external_currency", "external_unit_cost_cents IS NULL OR currency_id IS NOT NULL");
                    table.CheckConstraint("ck_marketplace_listing_external_supplier", "classification <> 'EXTERNAL_SUPPLIER' OR (supplier_name IS NOT NULL AND length(trim(supplier_name)) > 0)");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_order_items_external_cost_non_negative",
                table: "marketplace_order_items",
                sql: "\"external_unit_cost_cents_snapshot\" IS NULL OR \"external_unit_cost_cents_snapshot\" >= 0");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_listing_classification_current",
                table: "marketplace_listing_classification_versions",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "external_item_id", "external_variation_key" },
                unique: true,
                filter: "is_current = true");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_listing_classification_scope_version",
                table: "marketplace_listing_classification_versions",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "external_item_id", "external_variation_key", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_listing_classification_versions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_order_items_external_cost_non_negative",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "external_cost_currency_id",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "external_cost_version_id",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "external_supplier_name",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "external_unit_cost_cents_snapshot",
                table: "marketplace_order_items");
        }
    }
}
