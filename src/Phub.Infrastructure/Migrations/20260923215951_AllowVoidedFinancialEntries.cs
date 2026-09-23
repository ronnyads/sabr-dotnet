using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowVoidedFinancialEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_entry_status",
                table: "marketplace_financial_entries");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_entry_status",
                table: "marketplace_financial_entries",
                sql: "status IN ('ESTIMATED','CONFIRMED','VOIDED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_entry_status",
                table: "marketplace_financial_entries");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_entry_status",
                table: "marketplace_financial_entries",
                sql: "status IN ('ESTIMATED','CONFIRMED')");
        }
    }
}
