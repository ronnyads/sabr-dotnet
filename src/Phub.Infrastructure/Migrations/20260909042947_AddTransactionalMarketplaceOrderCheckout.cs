using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionalMarketplaceOrderCheckout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "additional_cents_at_payment",
                table: "marketplace_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "discount_cents_at_payment",
                table: "marketplace_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "freight_cents_at_payment",
                table: "marketplace_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_quote_hash",
                table: "marketplace_orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "product_subtotal_cents_at_payment",
                table: "marketplace_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "total_charge_cents_at_payment",
                table: "marketplace_orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "wallet_ledger_entry_id",
                table: "marketplace_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "catalog_unit_price_cents_at_payment",
                table: "marketplace_order_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "charge_line_total_cents_at_payment",
                table: "marketplace_order_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "cost_unit_price_cents_at_payment",
                table: "marketplace_order_items",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_wallet_ledger_order_debit",
                table: "wallet_ledger",
                columns: new[] { "order_id", "type" },
                unique: true,
                filter: "order_id IS NOT NULL AND type = 'Debit'");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_orders_wallet_ledger_entry",
                table: "marketplace_orders",
                column: "wallet_ledger_entry_id",
                unique: true,
                filter: "wallet_ledger_entry_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_order_items_payment_prices_non_negative",
                table: "marketplace_order_items",
                sql: "(\"catalog_unit_price_cents_at_payment\" IS NULL OR \"catalog_unit_price_cents_at_payment\" >= 0) AND (\"cost_unit_price_cents_at_payment\" IS NULL OR \"cost_unit_price_cents_at_payment\" >= 0) AND (\"charge_line_total_cents_at_payment\" IS NULL OR \"charge_line_total_cents_at_payment\" >= 0)");

            migrationBuilder.AddForeignKey(
                name: "FK_marketplace_orders_wallet_ledger_wallet_ledger_entry_id",
                table: "marketplace_orders",
                column: "wallet_ledger_entry_id",
                principalTable: "wallet_ledger",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_marketplace_orders_wallet_ledger_wallet_ledger_entry_id",
                table: "marketplace_orders");

            migrationBuilder.DropIndex(
                name: "ux_wallet_ledger_order_debit",
                table: "wallet_ledger");

            migrationBuilder.DropIndex(
                name: "ux_marketplace_orders_wallet_ledger_entry",
                table: "marketplace_orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_order_items_payment_prices_non_negative",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "additional_cents_at_payment",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "discount_cents_at_payment",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "freight_cents_at_payment",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "payment_quote_hash",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "product_subtotal_cents_at_payment",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "total_charge_cents_at_payment",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "wallet_ledger_entry_id",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "catalog_unit_price_cents_at_payment",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "charge_line_total_cents_at_payment",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "cost_unit_price_cents_at_payment",
                table: "marketplace_order_items");
        }
    }
}
