using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConvertMarketplaceSellerIdToLong : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE tenant_marketplace_listing_maps ALTER COLUMN seller_id TYPE bigint USING seller_id::bigint;");
            migrationBuilder.Sql("ALTER TABLE tenant_marketplace_connections ALTER COLUMN seller_id TYPE bigint USING seller_id::bigint;");
            migrationBuilder.Sql("ALTER TABLE marketplace_shipments ALTER COLUMN seller_id TYPE bigint USING seller_id::bigint;");
            migrationBuilder.Sql("ALTER TABLE marketplace_orders ALTER COLUMN seller_id TYPE bigint USING seller_id::bigint;");
            migrationBuilder.Sql("ALTER TABLE marketplace_order_items ALTER COLUMN seller_id TYPE bigint USING seller_id::bigint;");
            migrationBuilder.Sql("ALTER TABLE marketplace_event_logs ALTER COLUMN seller_id TYPE bigint USING seller_id::bigint;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE tenant_marketplace_listing_maps ALTER COLUMN seller_id TYPE character varying(80) USING seller_id::text;");
            migrationBuilder.Sql("ALTER TABLE tenant_marketplace_connections ALTER COLUMN seller_id TYPE character varying(80) USING seller_id::text;");
            migrationBuilder.Sql("ALTER TABLE marketplace_shipments ALTER COLUMN seller_id TYPE character varying(80) USING seller_id::text;");
            migrationBuilder.Sql("ALTER TABLE marketplace_orders ALTER COLUMN seller_id TYPE character varying(80) USING seller_id::text;");
            migrationBuilder.Sql("ALTER TABLE marketplace_order_items ALTER COLUMN seller_id TYPE character varying(80) USING seller_id::text;");
            migrationBuilder.Sql("ALTER TABLE marketplace_event_logs ALTER COLUMN seller_id TYPE character varying(80) USING seller_id::text;");
        }
    }
}
