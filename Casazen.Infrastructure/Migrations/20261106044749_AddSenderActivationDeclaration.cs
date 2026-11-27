using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSenderActivationDeclaration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActivationDeclaredAt",
                table: "PropertyComplianceSenders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivationDeclaredByUserId",
                table: "PropertyComplianceSenders",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivationDeclaredAt",
                table: "PropertyComplianceSenders");

            migrationBuilder.DropColumn(
                name: "ActivationDeclaredByUserId",
                table: "PropertyComplianceSenders");
        }
    }
}
