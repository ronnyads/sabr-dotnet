using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletDepositRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wallet_deposit_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_cents = table.Column<long>(type: "bigint", nullable: false),
                    method = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    proof_file_name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    proof_content_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    proof_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    client_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    review_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ledger_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_deposit_requests", x => x.Id);
                    table.CheckConstraint("ck_wallet_deposit_amount_positive", "amount_cents > 0");
                    table.CheckConstraint("ck_wallet_deposit_proof_size", "proof_size_bytes > 0 AND proof_size_bytes <= 10485760");
                    table.ForeignKey(
                        name: "FK_wallet_deposit_requests_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wallet_deposit_requests_wallet_ledger_ledger_entry_id",
                        column: x => x.ledger_entry_id,
                        principalTable: "wallet_ledger",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_deposit_proofs",
                columns: table => new
                {
                    deposit_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_deposit_proofs", x => x.deposit_request_id);
                    table.ForeignKey(
                        name: "FK_wallet_deposit_proofs_wallet_deposit_requests_deposit_reque~",
                        column: x => x.deposit_request_id,
                        principalTable: "wallet_deposit_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_deposit_client_history",
                table: "wallet_deposit_requests",
                columns: new[] { "tenant_id", "client_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_wallet_deposit_requests_client_id",
                table: "wallet_deposit_requests",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "IX_wallet_deposit_requests_ledger_entry_id",
                table: "wallet_deposit_requests",
                column: "ledger_entry_id",
                unique: true,
                filter: "ledger_entry_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_wallet_deposit_review_queue",
                table: "wallet_deposit_requests",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wallet_deposit_proofs");

            migrationBuilder.DropTable(
                name: "wallet_deposit_requests");
        }
    }
}
