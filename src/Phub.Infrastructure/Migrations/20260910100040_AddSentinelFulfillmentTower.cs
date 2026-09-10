using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSentinelFulfillmentTower : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attention_minutes",
                table: "tenant_marketplace_sla_rules",
                type: "integer",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.AddColumn<int>(
                name: "critical_minutes",
                table: "tenant_marketplace_sla_rules",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "monitor_minutes",
                table: "tenant_marketplace_sla_rules",
                type: "integer",
                nullable: false,
                defaultValue: 240);

            migrationBuilder.AddColumn<int>(
                name: "urgent_minutes",
                table: "tenant_marketplace_sla_rules",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.CreateTable(
                name: "marketplace_shipment_dispatch_deadline_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    shipment_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    dispatch_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    provider_last_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    queried_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_shipment_dispatch_deadline_versions", x => x.id);
                    table.CheckConstraint("ck_dispatch_deadline_version", "\"version\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "marketplace_shipment_external_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    shipment_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ml_order_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    status = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    substatus = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    shipping_mode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    logistic_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    handling_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ready_to_ship_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_printed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shipped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    not_delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    returned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivery_expected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delay_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    tracking_number = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    tracking_method = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    tracking_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    provider_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_marketplace_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    next_reconciliation_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconciliation_attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    freshness_state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    last_sync_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_shipment_external_states", x => x.id);
                    table.CheckConstraint("ck_shipment_external_version", "\"version\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "marketplace_shipment_operational_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    shipment_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    label_printed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    label_printed_by = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    picking_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    picking_started_by = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    separated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    separated_by = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    packed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    packed_by = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_shipment_operational_states", x => x.id);
                    table.CheckConstraint("ck_shipment_operational_version", "\"version\" > 0");
                });

            migrationBuilder.Sql("""
                INSERT INTO marketplace_shipment_external_states (
                    id, tenant_id, client_id, provider, seller_id, shipment_id, ml_order_id,
                    status, substatus, shipping_mode, logistic_type, shipped_at,
                    tracking_number, tracking_method, tracking_url, last_marketplace_sync_at,
                    payload_hash, version, next_reconciliation_at, reconciliation_attempts,
                    freshness_state, created_at, updated_at)
                SELECT gen_random_uuid(), s.tenant_id, s.client_id, s.provider, s.seller_id,
                    s.shipment_id, s.ml_order_id, s.status, s.substatus, s.shipping_mode,
                    s.logistic_type,
                    CASE WHEN lower(coalesce(s.status, '')) IN ('shipped','delivered','returned')
                         THEN s.shipped_at ELSE NULL END,
                    s.tracking_number, s.tracking_method, s.tracking_url,
                    s.updated_at, md5(concat_ws('|', s.status, s.substatus, s.updated_at::text)),
                    1, now(), 0, 'STALE', s.created_at, s.updated_at
                FROM marketplace_shipments s;

                INSERT INTO marketplace_shipment_operational_states (
                    id, tenant_id, client_id, provider, seller_id, shipment_id,
                    label_printed_at, picking_started_at, separated_at, packed_at,
                    version, created_at, updated_at)
                SELECT gen_random_uuid(), s.tenant_id, s.client_id, s.provider, s.seller_id,
                    s.shipment_id,
                    max(e.created_at) FILTER (WHERE e.topic = 'audit.fulfillment.label_printed'),
                    max(e.created_at) FILTER (WHERE e.topic IN ('audit.fulfillment.picking_started','audit.fulfillment.processing_started')),
                    max(e.created_at) FILTER (WHERE e.topic = 'audit.fulfillment.separated'),
                    max(e.created_at) FILTER (WHERE e.topic IN ('audit.fulfillment.packed','audit.fulfillment.processed','audit.fulfillment.dispatched')),
                    1, min(s.created_at), now()
                FROM marketplace_shipments s
                LEFT JOIN marketplace_event_logs e
                  ON e.tenant_id = s.tenant_id AND e.client_id = s.client_id
                 AND e.provider = s.provider AND e.seller_id = s.seller_id
                 AND e.resource_id = s.shipment_id
                GROUP BY s.tenant_id, s.client_id, s.provider, s.seller_id, s.shipment_id;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_sla_risk_thresholds",
                table: "tenant_marketplace_sla_rules",
                sql: "\"monitor_minutes\" > \"attention_minutes\" AND \"attention_minutes\" > \"urgent_minutes\" AND \"urgent_minutes\" > \"critical_minutes\" AND \"critical_minutes\" > 0");

            migrationBuilder.CreateIndex(
                name: "ix_dispatch_deadline_current_deadline",
                table: "marketplace_shipment_dispatch_deadline_versions",
                columns: new[] { "is_current", "dispatch_deadline" });

            migrationBuilder.CreateIndex(
                name: "ux_dispatch_deadline_current",
                table: "marketplace_shipment_dispatch_deadline_versions",
                columns: new[] { "tenant_id", "client_id", "provider", "shipment_id" },
                unique: true,
                filter: "\"is_current\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "ux_dispatch_deadline_scope_hash",
                table: "marketplace_shipment_dispatch_deadline_versions",
                columns: new[] { "tenant_id", "client_id", "provider", "shipment_id", "payload_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_dispatch_deadline_scope_version",
                table: "marketplace_shipment_dispatch_deadline_versions",
                columns: new[] { "tenant_id", "client_id", "provider", "shipment_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shipment_external_reconciliation",
                table: "marketplace_shipment_external_states",
                columns: new[] { "next_reconciliation_at", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "ix_shipment_external_scope_freshness",
                table: "marketplace_shipment_external_states",
                columns: new[] { "tenant_id", "client_id", "freshness_state", "last_marketplace_sync_at" });

            migrationBuilder.CreateIndex(
                name: "ux_shipment_external_scope_provider_shipment",
                table: "marketplace_shipment_external_states",
                columns: new[] { "tenant_id", "client_id", "provider", "shipment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_shipment_operational_scope_provider_shipment",
                table: "marketplace_shipment_operational_states",
                columns: new[] { "tenant_id", "client_id", "provider", "shipment_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_shipment_dispatch_deadline_versions");

            migrationBuilder.DropTable(
                name: "marketplace_shipment_external_states");

            migrationBuilder.DropTable(
                name: "marketplace_shipment_operational_states");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sla_risk_thresholds",
                table: "tenant_marketplace_sla_rules");

            migrationBuilder.DropColumn(
                name: "attention_minutes",
                table: "tenant_marketplace_sla_rules");

            migrationBuilder.DropColumn(
                name: "critical_minutes",
                table: "tenant_marketplace_sla_rules");

            migrationBuilder.DropColumn(
                name: "monitor_minutes",
                table: "tenant_marketplace_sla_rules");

            migrationBuilder.DropColumn(
                name: "urgent_minutes",
                table: "tenant_marketplace_sla_rules");
        }
    }
}
