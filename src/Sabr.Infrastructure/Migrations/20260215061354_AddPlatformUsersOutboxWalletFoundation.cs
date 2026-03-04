using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sabr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformUsersOutboxWalletFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    actor_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    entity = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    scope = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    response_json = table.Column<string>(type: "jsonb", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "platform_users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email_normalized = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    protheus_tag = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_users", x => x.Id);
                });

            migrationBuilder.Sql(@"
                INSERT INTO platform_users (""Id"", name, email, email_normalized, password_hash, role, protheus_tag, is_active, last_login_at, created_at, updated_at)
                SELECT
                    src.""Id"",
                    src.""Name"",
                    src.""Email"",
                    lower(src.""Email""),
                    src.""PasswordHash"",
                    CASE src.""Role""
                        WHEN 4 THEN 'SuperAdmin'
                        WHEN 1 THEN 'Admin'
                        WHEN 2 THEN 'Finance'
                        ELSE 'Admin'
                    END,
                    COALESCE(src.""ProtheusTag"", ''),
                    COALESCE(src.""IsActive"", true),
                    src.""LastLoginAt"",
                    now(),
                    now()
                FROM (
                    SELECT DISTINCT ON (lower(u.""Email""))
                        u.""Id"",
                        u.""Name"",
                        u.""Email"",
                        u.""PasswordHash"",
                        u.""Role"",
                        u.""ProtheusTag"",
                        u.""IsActive"",
                        u.""LastLoginAt"",
                        u.""CreatedAt""
                    FROM users u
                    WHERE u.""Role"" IN (1, 2, 4)
                    ORDER BY
                        lower(u.""Email""),
                        CASE u.""Role""
                            WHEN 4 THEN 3
                            WHEN 1 THEN 2
                            WHEN 2 THEN 1
                            ELSE 0
                        END DESC,
                        u.""CreatedAt"" DESC
                ) src;
            ");

            migrationBuilder.CreateTable(
                name: "protheus_outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    aggregate_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_protheus_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "wallet_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    balance_cents = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "wallet_ledger",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount_cents = table.Column<long>(type: "bigint", nullable: false),
                    balance_after_cents = table.Column<long>(type: "bigint", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    reference_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_ledger", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "platform_refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReplacedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByIp = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedByUserAgent = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_refresh_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_platform_refresh_tokens_platform_users_PlatformUserId",
                        column: x => x.PlatformUserId,
                        principalTable: "platform_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_request",
                table: "audit_events",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_tenant",
                table: "audit_events",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_idem_exp",
                table: "idempotency_keys",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_keys_tenant_id_scope_key",
                table: "idempotency_keys",
                columns: new[] { "tenant_id", "scope", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_refresh_tokens_PlatformUserId",
                table: "platform_refresh_tokens",
                column: "PlatformUserId");

            migrationBuilder.CreateIndex(
                name: "IX_platform_refresh_tokens_TokenHash",
                table: "platform_refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_users_email_normalized",
                table: "platform_users",
                column: "email_normalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_status_retry",
                table: "protheus_outbox",
                columns: new[] { "status", "next_retry_at" });

            migrationBuilder.CreateIndex(
                name: "ux_outbox_idem",
                table: "protheus_outbox",
                columns: new[] { "tenant_id", "correlation_id", "event_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wallet_accounts_tenant_id_client_id",
                table: "wallet_accounts",
                columns: new[] { "tenant_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ledger_client",
                table: "wallet_ledger",
                columns: new[] { "tenant_id", "client_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "platform_refresh_tokens");

            migrationBuilder.DropTable(
                name: "protheus_outbox");

            migrationBuilder.DropTable(
                name: "wallet_accounts");

            migrationBuilder.DropTable(
                name: "wallet_ledger");

            migrationBuilder.DropTable(
                name: "platform_users");
        }
    }
}
