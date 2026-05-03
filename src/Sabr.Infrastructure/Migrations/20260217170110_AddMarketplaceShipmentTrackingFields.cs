using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceShipmentTrackingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "shipped_at",
                table: "marketplace_shipments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "marketplace_shipments",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "substatus",
                table: "marketplace_shipments",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tracking_method",
                table: "marketplace_shipments",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tracking_number",
                table: "marketplace_shipments",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tracking_url",
                table: "marketplace_shipments",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "shipped_at",
                table: "marketplace_shipments");

            migrationBuilder.DropColumn(
                name: "status",
                table: "marketplace_shipments");

            migrationBuilder.DropColumn(
                name: "substatus",
                table: "marketplace_shipments");

            migrationBuilder.DropColumn(
                name: "tracking_method",
                table: "marketplace_shipments");

            migrationBuilder.DropColumn(
                name: "tracking_number",
                table: "marketplace_shipments");

            migrationBuilder.DropColumn(
                name: "tracking_url",
                table: "marketplace_shipments");
        }
    }
}
