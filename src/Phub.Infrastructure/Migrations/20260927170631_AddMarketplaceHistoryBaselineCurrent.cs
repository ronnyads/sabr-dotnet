using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceHistoryBaselineCurrent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "catalog_cost_baseline_id",
                table: "marketplace_order_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "plan_type",
                table: "financial_correction_plans",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "PRICE_VERSION_CORRECTION");

            migrationBuilder.CreateTable(
                name: "catalog_cost_baselines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    variant_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    baseline_price_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    baseline_unit_cost_cents = table.Column<long>(type: "bigint", nullable: false),
                    baseline_cut_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cost_origin = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    plan_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_cost_baselines", x => x.id);
                    table.CheckConstraint("ck_catalog_cost_baseline_amount", "baseline_unit_cost_cents > 0");
                    table.CheckConstraint("ck_catalog_cost_baseline_origin", "cost_origin = 'APPROVED_RETROACTIVE_BASELINE'");
                    table.CheckConstraint("ck_catalog_cost_baseline_status", "status IN ('STAGED','ACTIVE','DISCARDED')");
                    table.ForeignKey(
                        name: "FK_catalog_cost_baselines_financial_correction_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "financial_correction_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_catalog_cost_baselines_product_price_versions_baseline_pric~",
                        column: x => x.baseline_price_version_id,
                        principalTable: "product_price_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_product_price_versions_open_scope",
                table: "product_price_versions",
                columns: new[] { "product_sku", "variant_sku" },
                unique: true,
                filter: "valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_cost_baselines_plan_id",
                table: "catalog_cost_baselines",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_cost_baselines_price_version_id",
                table: "catalog_cost_baselines",
                column: "baseline_price_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_cost_baselines_variant_status",
                table: "catalog_cost_baselines",
                columns: new[] { "variant_sku", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_catalog_cost_baselines_active_variant",
                table: "catalog_cost_baselines",
                column: "variant_sku",
                unique: true,
                filter: "status = 'ACTIVE'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_cost_baselines");

            migrationBuilder.DropIndex(
                name: "ux_product_price_versions_open_scope",
                table: "product_price_versions");

            migrationBuilder.DropColumn(
                name: "catalog_cost_baseline_id",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "plan_type",
                table: "financial_correction_plans");
        }
    }
}
