using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakePlansGlobal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_plans_tenant_active",
                table: "plans");

            migrationBuilder.DropIndex(
                name: "ux_plans_tenant_name",
                table: "plans");

            migrationBuilder.DropIndex(
                name: "ix_plan_catalogs_tenant_catalog",
                table: "plan_catalogs");

            migrationBuilder.DropIndex(
                name: "ix_plan_catalogs_tenant_plan",
                table: "plan_catalogs");

            migrationBuilder.DropIndex(
                name: "ux_plan_catalogs_tenant_plan_catalog",
                table: "plan_catalogs");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "plan_catalogs");

            migrationBuilder.CreateIndex(
                name: "ix_plans_active",
                table: "plans",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ux_plans_name",
                table: "plans",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plan_catalogs_catalog",
                table: "plan_catalogs",
                column: "catalog_id");

            migrationBuilder.CreateIndex(
                name: "ix_plan_catalogs_plan",
                table: "plan_catalogs",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "ux_plan_catalogs_plan_catalog",
                table: "plan_catalogs",
                columns: new[] { "plan_id", "catalog_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_plans_active",
                table: "plans");

            migrationBuilder.DropIndex(
                name: "ux_plans_name",
                table: "plans");

            migrationBuilder.DropIndex(
                name: "ix_plan_catalogs_catalog",
                table: "plan_catalogs");

            migrationBuilder.DropIndex(
                name: "ix_plan_catalogs_plan",
                table: "plan_catalogs");

            migrationBuilder.DropIndex(
                name: "ux_plan_catalogs_plan_catalog",
                table: "plan_catalogs");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                table: "plans",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                table: "plan_catalogs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_plans_tenant_active",
                table: "plans",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_plans_tenant_name",
                table: "plans",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plan_catalogs_tenant_catalog",
                table: "plan_catalogs",
                columns: new[] { "tenant_id", "catalog_id" });

            migrationBuilder.CreateIndex(
                name: "ix_plan_catalogs_tenant_plan",
                table: "plan_catalogs",
                columns: new[] { "tenant_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "ux_plan_catalogs_tenant_plan_catalog",
                table: "plan_catalogs",
                columns: new[] { "tenant_id", "plan_id", "catalog_id" },
                unique: true);
        }
    }
}
