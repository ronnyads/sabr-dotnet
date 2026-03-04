using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sabr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientOnboardingStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OnboardingStep",
                table: "clients",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OnboardingStep",
                table: "clients");
        }
    }
}
