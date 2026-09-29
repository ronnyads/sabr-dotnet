using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReconcileParallelDocumentReviewStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE clients AS client
                SET "Status" = 3
                WHERE client."Status" = 2
                  AND (
                      SELECT COUNT(DISTINCT document."DocumentType")
                      FROM client_documents AS document
                      WHERE document."ClientId" = client."Id"
                        AND document."DocumentType" IN (1, 2, 3, 4)
                        AND document."Status" IN (2, 4)
                  ) = 4;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data reconciliation is intentionally not reversed.
        }
    }
}
