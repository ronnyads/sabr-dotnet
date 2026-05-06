using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phub.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientOutsideSpWarning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CnpjUf",
                table: "clients",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCnpjOutsideSp",
                table: "clients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "OutOfSpCnpjWarningAccepted",
                table: "clients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OutOfSpCnpjWarningAcceptedAt",
                table: "clients",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CnpjUf",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "IsCnpjOutsideSp",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "OutOfSpCnpjWarningAccepted",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "OutOfSpCnpjWarningAcceptedAt",
                table: "clients");
        }
    }
}
