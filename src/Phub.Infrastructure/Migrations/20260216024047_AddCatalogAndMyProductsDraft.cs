using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogAndMyProductsDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalogs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalogs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "client_plan_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_plan_subscriptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plan_catalogs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_catalogs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_catalogs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_sku = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_catalogs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product_price_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    product_sku = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    old_cost_price_cents = table.Column<long>(type: "bigint", nullable: false),
                    new_cost_price_cents = table.Column<long>(type: "bigint", nullable: false),
                    old_catalog_price_cents = table.Column<long>(type: "bigint", nullable: false),
                    new_catalog_price_cents = table.Column<long>(type: "bigint", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_price_history", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    sku = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    thumbnail_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cost_price_cents = table.Column<long>(type: "bigint", nullable: false),
                    catalog_price_cents = table.Column<long>(type: "bigint", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.sku);
                    table.CheckConstraint("ck_products_catalog_non_negative", "\"catalog_price_cents\" >= 0");
                    table.CheckConstraint("ck_products_cost_non_negative", "\"cost_price_cents\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "publications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_sku = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    pricing_mode = table.Column<int>(type: "integer", nullable: false),
                    markup_percent = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    fixed_price_cents = table.Column<long>(type: "bigint", nullable: true),
                    cost_price_cents_snapshot = table.Column<long>(type: "bigint", nullable: false),
                    catalog_price_cents_snapshot = table.Column<long>(type: "bigint", nullable: false),
                    final_price_cents_snapshot = table.Column<long>(type: "bigint", nullable: false),
                    price_snapshot_taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publications", x => x.id);
                    table.CheckConstraint("ck_publications_catalog_non_negative", "\"catalog_price_cents_snapshot\" >= 0");
                    table.CheckConstraint("ck_publications_cost_non_negative", "\"cost_price_cents_snapshot\" >= 0");
                    table.CheckConstraint("ck_publications_final_non_negative", "\"final_price_cents_snapshot\" >= 0");
                    table.CheckConstraint("ck_publications_fixed_non_negative", "\"fixed_price_cents\" IS NULL OR \"fixed_price_cents\" >= 0");
                    table.CheckConstraint("ck_publications_pricing_coherence", "(\"pricing_mode\" <> 2 OR \"fixed_price_cents\" IS NOT NULL) AND (\"pricing_mode\" <> 1 OR \"markup_percent\" IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_catalogs_tenant_active",
                table: "catalogs",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_catalogs_tenant_name",
                table: "catalogs",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_client_plan_subscriptions_lookup",
                table: "client_plan_subscriptions",
                columns: new[] { "tenant_id", "client_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_client_plan_subscriptions_tenant_client_plan_starts",
                table: "client_plan_subscriptions",
                columns: new[] { "tenant_id", "client_id", "plan_id", "starts_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plan_catalogs_tenant_catalog",
                table: "plan_catalogs",
                columns: new[] { "tenant_id", "catalog_id" });

            migrationBuilder.CreateIndex(
                name: "ix_plan_catalogs_tenant_plan",
                table: "plan_catalogs",
                columns: new[] { "tenant_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "ux_plan_catalogs_tenant_plan_catalog",
                table: "plan_catalogs",
                columns: new[] { "tenant_id", "plan_id", "catalog_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plans_tenant_active",
                table: "plans",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_plans_tenant_name",
                table: "plans",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_catalogs_tenant_sku",
                table: "product_catalogs",
                columns: new[] { "tenant_id", "product_sku" });

            migrationBuilder.CreateIndex(
                name: "ux_product_catalogs_tenant_catalog_sku",
                table: "product_catalogs",
                columns: new[] { "tenant_id", "catalog_id", "product_sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_price_history_sku_changed",
                table: "product_price_history",
                columns: new[] { "product_sku", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_products_name",
                table: "products",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_publications_tenant_client_status_updated",
                table: "publications",
                columns: new[] { "tenant_id", "client_id", "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ux_publications_draft_tenant_client_sku",
                table: "publications",
                columns: new[] { "tenant_id", "client_id", "product_sku" },
                unique: true,
                filter: "\"status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalogs");

            migrationBuilder.DropTable(
                name: "client_plan_subscriptions");

            migrationBuilder.DropTable(
                name: "plan_catalogs");

            migrationBuilder.DropTable(
                name: "plans");

            migrationBuilder.DropTable(
                name: "product_catalogs");

            migrationBuilder.DropTable(
                name: "product_price_history");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "publications");
        }
    }
}
