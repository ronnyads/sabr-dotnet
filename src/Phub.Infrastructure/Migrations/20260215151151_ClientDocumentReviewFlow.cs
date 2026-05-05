using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClientDocumentReviewFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_client_documents_ClientId",
                table: "client_documents");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RequestedAt",
                table: "client_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewReason",
                table: "client_documents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT
                        "Id",
                        "ClientId",
                        "DocumentType",
                        ROW_NUMBER() OVER (
                            PARTITION BY "ClientId", "DocumentType"
                            ORDER BY
                                COALESCE("SubmittedAt", "UpdatedAt", "CreatedAt") DESC,
                                "UpdatedAt" DESC NULLS LAST,
                                "CreatedAt" DESC NULLS LAST,
                                "Id" DESC
                        ) AS rn
                    FROM client_documents
                )
                DELETE FROM client_documents cd
                USING ranked r
                WHERE cd."Id" = r."Id"
                  AND r.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_client_documents_ClientId_DocumentType",
                table: "client_documents",
                columns: new[] { "ClientId", "DocumentType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_client_documents_ClientId_DocumentType",
                table: "client_documents");

            migrationBuilder.DropColumn(
                name: "RequestedAt",
                table: "client_documents");

            migrationBuilder.DropColumn(
                name: "ReviewReason",
                table: "client_documents");

            migrationBuilder.CreateIndex(
                name: "IX_client_documents_ClientId",
                table: "client_documents",
                column: "ClientId");
        }
    }
}
