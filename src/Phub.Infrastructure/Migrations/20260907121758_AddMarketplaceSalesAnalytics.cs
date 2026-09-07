using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceSalesAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "channel_created_at",
                table: "marketplace_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "currency_id",
                table: "marketplace_orders",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "paid_amount",
                table: "marketplace_orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "total_amount",
                table: "marketplace_orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "channel_sku",
                table: "marketplace_order_items",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "currency_id",
                table: "marketplace_order_items",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "full_unit_price",
                table: "marketplace_order_items",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "gross_price",
                table: "marketplace_order_items",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "product_name",
                table: "marketplace_order_items",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "sale_fee",
                table: "marketplace_order_items",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "unit_price",
                table: "marketplace_order_items",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // Backfill the structured analytics fields from the immutable payloads already
            // stored by the integration. Defensive regex checks keep malformed legacy JSON
            // from blocking the production migration.
            migrationBuilder.Sql("""
                UPDATE marketplace_orders
                SET channel_created_at = CASE
                        WHEN raw_json->>'date_created' ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T' THEN (raw_json->>'date_created')::timestamptz
                        ELSE channel_created_at
                    END,
                    currency_id = LEFT(NULLIF(raw_json->>'currency_id', ''), 3),
                    total_amount = CASE
                        WHEN raw_json->>'total_amount' ~ '^[0-9]+([.][0-9]+)?$' THEN (raw_json->>'total_amount')::numeric(18,2)
                        ELSE total_amount
                    END,
                    paid_amount = CASE
                        WHEN raw_json->>'paid_amount' ~ '^[0-9]+([.][0-9]+)?$' THEN (raw_json->>'paid_amount')::numeric(18,2)
                        ELSE paid_amount
                    END
                WHERE raw_json IS NOT NULL;

                UPDATE marketplace_order_items
                SET channel_sku = LEFT(COALESCE(
                        NULLIF(raw_json->'item'->>'seller_sku', ''),
                        NULLIF(raw_json->'item'->>'seller_custom_field', ''),
                        (
                            SELECT NULLIF(attribute->>'value_name', '')
                            FROM jsonb_array_elements(
                                CASE
                                    WHEN jsonb_typeof(raw_json->'item'->'attributes') = 'array'
                                        THEN raw_json->'item'->'attributes'
                                    ELSE '[]'::jsonb
                                END
                            ) attribute
                            WHERE UPPER(attribute->>'id') = 'SELLER_SKU'
                            LIMIT 1
                        )
                    ), 120),
                    product_name = LEFT(NULLIF(raw_json->'item'->>'title', ''), 300),
                    currency_id = LEFT(NULLIF(raw_json->>'currency_id', ''), 3),
                    unit_price = CASE
                        WHEN raw_json->>'unit_price' ~ '^[0-9]+([.][0-9]+)?$' THEN (raw_json->>'unit_price')::numeric(18,2)
                        ELSE unit_price
                    END,
                    full_unit_price = CASE
                        WHEN raw_json->>'full_unit_price' ~ '^[0-9]+([.][0-9]+)?$' THEN (raw_json->>'full_unit_price')::numeric(18,2)
                        ELSE full_unit_price
                    END,
                    gross_price = CASE
                        WHEN raw_json->>'gross_price' ~ '^[0-9]+([.][0-9]+)?$' THEN (raw_json->>'gross_price')::numeric(18,2)
                        ELSE gross_price
                    END,
                    sale_fee = CASE
                        WHEN raw_json->>'sale_fee' ~ '^[0-9]+([.][0-9]+)?$' THEN (raw_json->>'sale_fee')::numeric(18,2)
                        ELSE sale_fee
                    END
                WHERE raw_json IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_orders_scope_channel_created",
                table: "marketplace_orders",
                columns: new[] { "tenant_id", "client_id", "channel_created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_order_items_scope_sku",
                table: "marketplace_order_items",
                columns: new[] { "tenant_id", "client_id", "sabr_variant_sku" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_order_items_prices_non_negative",
                table: "marketplace_order_items",
                sql: "(\"unit_price\" IS NULL OR \"unit_price\" >= 0) AND (\"full_unit_price\" IS NULL OR \"full_unit_price\" >= 0) AND (\"gross_price\" IS NULL OR \"gross_price\" >= 0) AND (\"sale_fee\" IS NULL OR \"sale_fee\" >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_marketplace_orders_scope_channel_created",
                table: "marketplace_orders");

            migrationBuilder.DropIndex(
                name: "ix_marketplace_order_items_scope_sku",
                table: "marketplace_order_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_order_items_prices_non_negative",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "channel_created_at",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "currency_id",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "paid_amount",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "total_amount",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "channel_sku",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "currency_id",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "full_unit_price",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "gross_price",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "product_name",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "sale_fee",
                table: "marketplace_order_items");

            migrationBuilder.DropColumn(
                name: "unit_price",
                table: "marketplace_order_items");
        }
    }
}
