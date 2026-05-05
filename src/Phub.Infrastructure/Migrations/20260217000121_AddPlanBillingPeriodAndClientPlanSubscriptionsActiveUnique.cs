using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanBillingPeriodAndClientPlanSubscriptionsActiveUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "billing_period",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_plans_billing_period_valid",
                table: "plans",
                sql: "\"billing_period\" IN (0, 1, 2, 3)");

            migrationBuilder.Sql("""
                UPDATE client_plan_subscriptions cps
                SET ends_at = cps.starts_at +
                    CASE p.billing_period
                        WHEN 0 THEN INTERVAL '1 month'
                        WHEN 1 THEN INTERVAL '3 months'
                        WHEN 2 THEN INTERVAL '6 months'
                        WHEN 3 THEN INTERVAL '12 months'
                        ELSE INTERVAL '1 month'
                    END,
                    updated_at = NOW()
                FROM plans p
                WHERE cps.tenant_id = p.tenant_id
                  AND cps.plan_id = p.id
                  AND cps.is_active = true
                  AND cps.ends_at IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE client_plan_subscriptions
                SET ends_at = starts_at + INTERVAL '1 month',
                    updated_at = NOW()
                WHERE is_active = true
                  AND ends_at IS NULL;
                """);

            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT id,
                           ROW_NUMBER() OVER (
                               PARTITION BY tenant_id, client_id, plan_id
                               ORDER BY starts_at DESC, created_at DESC, id DESC
                           ) AS rn
                    FROM client_plan_subscriptions
                    WHERE is_active = true
                )
                UPDATE client_plan_subscriptions cps
                SET is_active = false,
                    ends_at = COALESCE(cps.ends_at, NOW()),
                    updated_at = NOW()
                FROM ranked
                WHERE cps.id = ranked.id
                  AND ranked.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_client_plan_subscriptions_active_tenant_client_plan",
                table: "client_plan_subscriptions",
                columns: new[] { "tenant_id", "client_id", "plan_id" },
                unique: true,
                filter: "\"is_active\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_plans_billing_period_valid",
                table: "plans");

            migrationBuilder.DropIndex(
                name: "ux_client_plan_subscriptions_active_tenant_client_plan",
                table: "client_plan_subscriptions");

            migrationBuilder.DropColumn(
                name: "billing_period",
                table: "plans");
        }
    }
}
