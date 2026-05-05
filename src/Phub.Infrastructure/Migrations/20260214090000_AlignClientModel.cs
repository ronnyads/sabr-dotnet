using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    public partial class AlignClientModel : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_users_sellers_SellerId",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_SellerId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "users");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropForeignKey(
                name: "FK_seller_documents_sellers_SellerId",
                table: "seller_documents");

            migrationBuilder.RenameTable(
                name: "sellers",
                newName: "clients");

            migrationBuilder.RenameTable(
                name: "seller_documents",
                newName: "client_documents");

            migrationBuilder.RenameColumn(
                name: "SellerId",
                table: "client_documents",
                newName: "ClientId");

            migrationBuilder.RenameIndex(
                name: "IX_seller_documents_SellerId",
                table: "client_documents",
                newName: "IX_client_documents_ClientId");

            migrationBuilder.RenameIndex(
                name: "IX_seller_documents_TenantId",
                table: "client_documents",
                newName: "IX_client_documents_TenantId");

            migrationBuilder.RenameIndex(
                name: "IX_sellers_Document",
                table: "clients",
                newName: "IX_clients_Document");

            migrationBuilder.RenameIndex(
                name: "IX_sellers_TenantId",
                table: "clients",
                newName: "IX_clients_TenantId");

            migrationBuilder.AddColumn<string>(
                name: "ProtheusCode",
                table: "clients",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "client_stores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    ProtheusTag = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProtheusOperation = table.Column<int>(type: "integer", nullable: false),
                    ProtheusRef = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ProtheusLastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_stores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_client_stores_clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_client_stores_ClientId_StoreCode",
                table: "client_stores",
                columns: new[] { "ClientId", "StoreCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_client_stores_TenantId",
                table: "client_stores",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_client_documents_clients_ClientId",
                table: "client_documents",
                column: "ClientId",
                principalTable: "clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql(@"
                UPDATE users
                SET ""Role"" = 1
                WHERE ""Role"" = 3;

                UPDATE users
                SET ""SectorCode"" = 'SIGAGPE'
                WHERE ""SectorCode"" IS NULL OR ""SectorCode"" = '' OR ""SectorCode"" = 'SELLER';

                UPDATE users
                SET ""ProtheusTag"" = REPLACE(""ProtheusTag"", 'SELLER_', 'SIGAGPE_')
                WHERE ""ProtheusTag"" LIKE 'SELLER_%';

                UPDATE clients
                SET ""ProtheusTag"" = REPLACE(""ProtheusTag"", 'SELLER_', 'SA1_')
                WHERE ""ProtheusTag"" LIKE 'SELLER_%';

                UPDATE client_documents
                SET ""ProtheusTag"" = REPLACE(""ProtheusTag"", 'SELLER_', 'SA1_')
                WHERE ""ProtheusTag"" LIKE 'SELLER_%';
            ");

            migrationBuilder.Sql(@"
                UPDATE clients c
                SET ""ProtheusCode"" = t.""Slug""
                FROM tenants t
                WHERE t.""Id"" = c.""TenantId"" AND t.""Slug"" IS NOT NULL AND t.""Slug"" <> '';

                WITH max_code AS (
                    SELECT COALESCE(MAX(CASE WHEN ""ProtheusCode"" ~ '^[0-9]+$' THEN ""ProtheusCode""::int END), 0) AS max_val
                    FROM clients
                ),
                missing AS (
                    SELECT ""Id"", ROW_NUMBER() OVER (ORDER BY ""CreatedAt"", ""Id"") + (SELECT max_val FROM max_code) AS rn
                    FROM clients
                    WHERE ""ProtheusCode"" IS NULL OR ""ProtheusCode"" = ''
                )
                UPDATE clients c
                SET ""ProtheusCode"" = LPAD(missing.rn::text, 6, '0')
                FROM missing
                WHERE c.""Id"" = missing.""Id"";
            ");

            migrationBuilder.Sql(@"
                CREATE SEQUENCE IF NOT EXISTS client_protheus_code_seq;

                WITH max_code AS (
                    SELECT COALESCE(MAX(CASE WHEN ""ProtheusCode"" ~ '^[0-9]+$' THEN ""ProtheusCode""::int END), 0) AS max_val
                    FROM clients
                )
                SELECT setval(
                    'client_protheus_code_seq',
                    CASE WHEN max_val < 1 THEN 1 ELSE max_val END,
                    CASE WHEN max_val < 1 THEN false ELSE true END
                )
                FROM max_code;
            ");

            migrationBuilder.Sql(@"
                CREATE EXTENSION IF NOT EXISTS ""pgcrypto"";

                INSERT INTO client_stores (
                    ""Id"",
                    ""ClientId"",
                    ""StoreCode"",
                    ""Name"",
                    ""IsActive"",
                    ""TenantId"",
                    ""ProtheusTag"",
                    ""ProtheusOperation"",
                    ""ProtheusRef"",
                    ""ProtheusLastSyncAt"",
                    ""CreatedAt"",
                    ""UpdatedAt"")
                SELECT gen_random_uuid(), c.""Id"", '01', NULL, TRUE, c.""TenantId"", 'SA1_CREATE', 1, NULL, NULL, NOW(), NOW()
                FROM clients c;

                INSERT INTO tenants (""Id"", ""Name"", ""Slug"", ""Status"", ""CreatedAt"")
                SELECT c.""TenantId"", c.""LegalName"", c.""ProtheusCode"", 1, NOW()
                FROM clients c
                LEFT JOIN tenants t ON t.""Id"" = c.""TenantId""
                WHERE t.""Id"" IS NULL;
            ");

            migrationBuilder.AlterColumn<string>(
                name: "ProtheusCode",
                table: "clients",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_clients_ProtheusCode",
                table: "clients",
                column: "ProtheusCode",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_client_documents_clients_ClientId",
                table: "client_documents");

            migrationBuilder.DropTable(
                name: "client_stores");

            migrationBuilder.DropIndex(
                name: "IX_clients_ProtheusCode",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "ProtheusCode",
                table: "clients");

            migrationBuilder.RenameIndex(
                name: "IX_clients_Document",
                table: "clients",
                newName: "IX_sellers_Document");

            migrationBuilder.RenameIndex(
                name: "IX_clients_TenantId",
                table: "clients",
                newName: "IX_sellers_TenantId");

            migrationBuilder.RenameTable(
                name: "clients",
                newName: "sellers");

            migrationBuilder.RenameColumn(
                name: "ClientId",
                table: "client_documents",
                newName: "SellerId");

            migrationBuilder.RenameIndex(
                name: "IX_client_documents_ClientId",
                table: "client_documents",
                newName: "IX_seller_documents_SellerId");

            migrationBuilder.RenameIndex(
                name: "IX_client_documents_TenantId",
                table: "client_documents",
                newName: "IX_seller_documents_TenantId");

            migrationBuilder.RenameTable(
                name: "client_documents",
                newName: "seller_documents");

            migrationBuilder.AddColumn<Guid>(
                name: "SellerId",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_SellerId",
                table: "users",
                column: "SellerId");

            migrationBuilder.AddForeignKey(
                name: "FK_users_sellers_SellerId",
                table: "users",
                column: "SellerId",
                principalTable: "sellers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_seller_documents_sellers_SellerId",
                table: "seller_documents",
                column: "SellerId",
                principalTable: "sellers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonType = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TradeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Document = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StateRegistration = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    IsStateRegistrationExempt = table.Column<bool>(type: "boolean", nullable: false),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Whatsapp = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    BirthDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ZipCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    District = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Complement = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    ProtheusTag = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProtheusOperation = table.Column<int>(type: "integer", nullable: false),
                    ProtheusRef = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ProtheusLastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customers_Document",
                table: "customers",
                column: "Document");

            migrationBuilder.CreateIndex(
                name: "IX_customers_TenantId",
                table: "customers",
                column: "TenantId");

            migrationBuilder.Sql(@"DROP SEQUENCE IF EXISTS client_protheus_code_seq;");
        }
    }
}
