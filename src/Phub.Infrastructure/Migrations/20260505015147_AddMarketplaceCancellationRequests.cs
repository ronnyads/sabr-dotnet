using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceCancellationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cancellation_request_reason",
                table: "marketplace_orders",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_request_status",
                table: "marketplace_orders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancellation_requested_at",
                table: "marketplace_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_requested_by",
                table: "marketplace_orders",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancellation_reviewed_at",
                table: "marketplace_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_reviewed_by",
                table: "marketplace_orders",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cancellation_request_reason",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "cancellation_request_status",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "cancellation_requested_at",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "cancellation_requested_by",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "cancellation_reviewed_at",
                table: "marketplace_orders");

            migrationBuilder.DropColumn(
                name: "cancellation_reviewed_by",
                table: "marketplace_orders");
        }
    }
}
