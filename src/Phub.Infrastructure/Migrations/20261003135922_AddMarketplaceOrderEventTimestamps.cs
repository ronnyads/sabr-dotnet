using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceOrderEventTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at",
                table: "marketplace_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_updated_at",
                table: "marketplace_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_orders_scope_cancelled_at",
                table: "marketplace_orders",
                columns: new[] { "tenant_id", "client_id", "provider", "cancelled_at" },
                filter: "cancelled_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_marketplace_orders_scope_cancelled_at",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "provider_updated_at",
                table: "marketplace_orders");
        }
    }
}
