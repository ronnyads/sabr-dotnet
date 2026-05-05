using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceEnterprisePhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "marketplace_event_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    topic = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    notification_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    dedupe_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_event_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_shipments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    shipment_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ml_order_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    shipping_mode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    logistic_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ship_by_deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    label_internal_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    label_source_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    label_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    label_content_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    label_content_bytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_shipments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_marketplace_sla_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    logistic_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    shipping_mode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    cutoff_local_time = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_marketplace_sla_rules", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_event_logs_scope_seller",
                table: "marketplace_event_logs",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_event_logs_status_created",
                table: "marketplace_event_logs",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_event_logs_dedupe_key",
                table: "marketplace_event_logs",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_shipments_scope_order",
                table: "marketplace_shipments",
                columns: new[] { "tenant_id", "client_id", "provider", "ml_order_id" });

            migrationBuilder.CreateIndex(
                name: "ux_marketplace_shipments_scope_provider_shipment",
                table: "marketplace_shipments",
                columns: new[] { "tenant_id", "client_id", "provider", "shipment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tenant_marketplace_sla_rules_scope_mode",
                table: "tenant_marketplace_sla_rules",
                columns: new[] { "tenant_id", "client_id", "provider", "logistic_type", "shipping_mode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_event_logs");

            migrationBuilder.DropTable(
                name: "marketplace_shipments");

            migrationBuilder.DropTable(
                name: "tenant_marketplace_sla_rules");
        }
    }
}
