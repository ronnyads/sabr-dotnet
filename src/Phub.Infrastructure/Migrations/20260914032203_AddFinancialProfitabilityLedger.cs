using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialProfitabilityLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_reconciliation_cursors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    billing_group = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    period_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    from_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    last_payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_succeeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_financial_reconciliation_cursors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "financial_sync_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    job_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    range_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    range_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checkpoint = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    dedupe_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    processed = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_by = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_financial_sync_jobs", x => x.id);
                    table.CheckConstraint("ck_financial_sync_range", "range_to > range_from");
                    table.ForeignKey(
                        name: "FK_financial_sync_jobs_financial_sync_jobs_parent_job_id",
                        column: x => x.parent_job_id,
                        principalTable: "financial_sync_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_financial_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    entry_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    layer = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount_cents = table.Column<long>(type: "bigint", nullable: false),
                    currency_id = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    economic_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    supersedes_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    marketplace_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    marketplace_order_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_order_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    external_payment_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    external_shipment_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    external_pack_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    external_claim_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    external_return_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    economic_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    financial_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_endpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    source_record_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    canonical_payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_financial_entries", x => x.id);
                    table.CheckConstraint("ck_financial_entry_amount_non_zero", "amount_cents <> 0");
                    table.CheckConstraint("ck_financial_entry_layer", "layer IN ('OPERATIONAL','RECONCILED','INTERNAL_CONFIRMED')");
                    table.CheckConstraint("ck_financial_entry_status", "status IN ('ESTIMATED','CONFIRMED')");
                    table.ForeignKey(
                        name: "FK_marketplace_financial_entries_marketplace_financial_entries~",
                        column: x => x.supersedes_entry_id,
                        principalTable: "marketplace_financial_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_marketplace_financial_entries_marketplace_order_items_marke~",
                        column: x => x.marketplace_order_item_id,
                        principalTable: "marketplace_order_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_marketplace_financial_entries_marketplace_orders_marketplac~",
                        column: x => x.marketplace_order_id,
                        principalTable: "marketplace_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_oauth_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    app_family = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    client_id_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    access_token_protected = table.Column<string>(type: "text", nullable: false),
                    refresh_token_protected = table.Column<string>(type: "text", nullable: false),
                    token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    scopes_json = table.Column<string>(type: "jsonb", nullable: false),
                    capabilities_json = table.Column<string>(type: "jsonb", nullable: false),
                    last_capability_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    capability_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    requires_reauthorization = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_oauth_grants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_order_financial_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    marketplace_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    maturity = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    gross_revenue_cents = table.Column<long>(type: "bigint", nullable: false),
                    estimated_economic_net_cents = table.Column<long>(type: "bigint", nullable: false),
                    confirmed_value_cents = table.Column<long>(type: "bigint", nullable: false),
                    operational_profit_cents = table.Column<long>(type: "bigint", nullable: false),
                    unallocated_cents = table.Column<long>(type: "bigint", nullable: false),
                    sku_resolved = table.Column<bool>(type: "boolean", nullable: false),
                    cost_resolved = table.Column<bool>(type: "boolean", nullable: false),
                    freight_resolved = table.Column<bool>(type: "boolean", nullable: false),
                    operational_components_resolved = table.Column<bool>(type: "boolean", nullable: false),
                    confirmed_components_resolved = table.Column<bool>(type: "boolean", nullable: false),
                    item_allocation_resolved = table.Column<bool>(type: "boolean", nullable: false),
                    incomplete_reasons_json = table.Column<string>(type: "jsonb", nullable: false),
                    divergence_json = table.Column<string>(type: "jsonb", nullable: false),
                    was_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reopened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_projected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_order_financial_states", x => x.id);
                    table.CheckConstraint("ck_order_financial_state_maturity", "maturity IN ('INCOMPLETO','ESTIMADO','PARCIALMENTE_CONFIRMADO','CONFIRMADO','REABERTO')");
                    table.CheckConstraint("ck_order_financial_state_version", "version > 0");
                    table.ForeignKey(
                        name: "FK_marketplace_order_financial_states_marketplace_orders_marke~",
                        column: x => x.marketplace_order_id,
                        principalTable: "marketplace_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "seller_tax_profile_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    rate_basis_points = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_by = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seller_tax_profile_versions", x => x.id);
                    table.CheckConstraint("ck_seller_tax_period", "effective_to IS NULL OR effective_to > effective_from");
                    table.CheckConstraint("ck_seller_tax_rate", "rate_basis_points >= 0 AND rate_basis_points <= 10000");
                    table.CheckConstraint("ck_seller_tax_version", "version > 0");
                });

            migrationBuilder.CreateTable(
                name: "financial_economic_heads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    seller_id = table.Column<long>(type: "bigint", nullable: false),
                    economic_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    active_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_financial_economic_heads", x => x.id);
                    table.CheckConstraint("ck_financial_head_version", "version > 0");
                    table.ForeignKey(
                        name: "FK_financial_economic_heads_marketplace_financial_entries_acti~",
                        column: x => x.active_entry_id,
                        principalTable: "marketplace_financial_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_financial_heads_active_entry",
                table: "financial_economic_heads",
                column: "active_entry_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_financial_heads_scope_economic_key",
                table: "financial_economic_heads",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "economic_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_cursor_seller_retry",
                table: "financial_reconciliation_cursors",
                columns: new[] { "seller_id", "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ux_financial_cursor_scope_period",
                table: "financial_reconciliation_cursors",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "billing_group", "period_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_sync_jobs_parent",
                table: "financial_sync_jobs",
                column: "parent_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_sync_jobs_pending",
                table: "financial_sync_jobs",
                columns: new[] { "seller_id", "next_attempt_at", "created_at" },
                filter: "\"status\" IN ('PENDING','RETRY')");

            migrationBuilder.CreateIndex(
                name: "ux_financial_sync_jobs_scope_dedupe",
                table: "financial_sync_jobs",
                columns: new[] { "tenant_id", "provider", "seller_id", "dedupe_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_entries_scope_confirmed_at",
                table: "marketplace_financial_entries",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "financial_confirmed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_entries_scope_economic_at",
                table: "marketplace_financial_entries",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "economic_occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_entries_seller_order",
                table: "marketplace_financial_entries",
                columns: new[] { "seller_id", "marketplace_order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_entries_seller_payment",
                table: "marketplace_financial_entries",
                columns: new[] { "seller_id", "external_payment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_entries_seller_shipment",
                table: "marketplace_financial_entries",
                columns: new[] { "seller_id", "external_shipment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_entries_supersedes",
                table: "marketplace_financial_entries",
                column: "supersedes_entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_financial_entries_marketplace_order_id",
                table: "marketplace_financial_entries",
                column: "marketplace_order_id");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_financial_entries_marketplace_order_item_id",
                table: "marketplace_financial_entries",
                column: "marketplace_order_item_id");

            migrationBuilder.CreateIndex(
                name: "ux_financial_entries_scope_idempotency",
                table: "marketplace_financial_entries",
                columns: new[] { "tenant_id", "provider", "seller_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_grants_seller_verified",
                table: "marketplace_oauth_grants",
                columns: new[] { "seller_id", "last_capability_verified_at" });

            migrationBuilder.CreateIndex(
                name: "ux_oauth_grants_scope_app_family",
                table: "marketplace_oauth_grants",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "app_family" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_financial_states_scope_maturity",
                table: "marketplace_order_financial_states",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "maturity" });

            migrationBuilder.CreateIndex(
                name: "ux_order_financial_states_order",
                table: "marketplace_order_financial_states",
                column: "marketplace_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seller_tax_scope_effective_from",
                table: "seller_tax_profile_versions",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ux_seller_tax_scope_version",
                table: "seller_tax_profile_versions",
                columns: new[] { "tenant_id", "client_id", "provider", "seller_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_economic_heads");

            migrationBuilder.DropTable(
                name: "financial_reconciliation_cursors");

            migrationBuilder.DropTable(
                name: "financial_sync_jobs");

            migrationBuilder.DropTable(
                name: "marketplace_oauth_grants");

            migrationBuilder.DropTable(
                name: "marketplace_order_financial_states");

            migrationBuilder.DropTable(
                name: "seller_tax_profile_versions");

            migrationBuilder.DropTable(
                name: "marketplace_financial_entries");
        }
    }
}
