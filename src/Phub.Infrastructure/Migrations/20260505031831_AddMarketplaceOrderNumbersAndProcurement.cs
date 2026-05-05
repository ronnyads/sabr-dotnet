using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceOrderNumbersAndProcurement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "shipment_scan_code",
                table: "marketplace_shipments",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "internal_order_number",
                table: "marketplace_orders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "marketplace_order_number_sequences",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    next_number = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_order_number_sequences", x => x.id);
                });

            migrationBuilder.Sql(
                """
                WITH ordered_orders AS (
                    SELECT
                        id,
                        ROW_NUMBER() OVER (
                            ORDER BY COALESCE(imported_at, created_at), created_at, id
                        ) AS seq
                    FROM marketplace_orders
                    WHERE internal_order_number IS NULL
                )
                UPDATE marketplace_orders AS mo
                SET internal_order_number = 'PHUB-' || LPAD(ordered_orders.seq::text, 8, '0')
                FROM ordered_orders
                WHERE mo.id = ordered_orders.id;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO marketplace_order_number_sequences (id, next_number, updated_at)
                VALUES (
                    1,
                    COALESCE(
                        (
                            SELECT MAX(CAST(SUBSTRING(internal_order_number FROM '[0-9]+$') AS bigint)) + 1
                            FROM marketplace_orders
                            WHERE internal_order_number LIKE 'PHUB-%'
                        ),
                        1
                    ),
                    NOW()
                );
                """);

            migrationBuilder.Sql(
                """
                UPDATE marketplace_shipments AS ms
                SET shipment_scan_code = 'PHUB-SCAN-' || UPPER(SUBSTRING(MD5(
                    mo.id::text || '|' || ms.id::text || '|' || COALESCE(ms.shipment_id, '') || '|' ||
                    COALESCE(mo.internal_order_number, mo.ml_order_id, '')
                ) FROM 1 FOR 12))
                FROM marketplace_orders AS mo
                WHERE mo.id = ms.marketplace_order_id
                  AND ms.shipment_scan_code IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_shipments_scan_code",
                table: "marketplace_shipments",
                column: "shipment_scan_code");

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_orders_internal_order_number",
                table: "marketplace_orders",
                column: "internal_order_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_order_number_sequences");

            migrationBuilder.DropIndex(
                name: "ix_marketplace_shipments_scan_code",
                table: "marketplace_shipments");

            migrationBuilder.DropIndex(
                name: "ux_marketplace_orders_internal_order_number",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "shipment_scan_code",
                table: "marketplace_shipments");

            migrationBuilder.DropColumn(
                name: "internal_order_number",
                table: "marketplace_orders");
        }
    }
}
