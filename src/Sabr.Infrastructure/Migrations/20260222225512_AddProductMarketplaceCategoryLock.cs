using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sabr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductMarketplaceCategoryLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_marketplace_category_lock",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_product_sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    site_id = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    approved_category_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    approved_category_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    approved_category_path = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    internal_category_slug_snapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_marketplace_category_lock", x => x.id);
                    table.CheckConstraint("ck_product_marketplace_category_lock_site_format", "\"site_id\" ~ '^ML[A-Z]$'");
                    table.CheckConstraint("ck_product_marketplace_category_lock_sku_format", "\"base_product_sku\" ~ '^[A-Z0-9][A-Z0-9_/-]{0,63}$'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_product_marketplace_category_lock_scope_site_status",
                table: "product_marketplace_category_lock",
                columns: new[] { "tenant_id", "client_id", "site_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_product_marketplace_category_lock_scope_product_site",
                table: "product_marketplace_category_lock",
                columns: new[] { "tenant_id", "client_id", "base_product_sku", "site_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_marketplace_category_lock");
        }
    }
}
