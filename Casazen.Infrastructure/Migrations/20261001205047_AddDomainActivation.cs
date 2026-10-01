using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DomainCheckFailures",
                table: "Orgs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DomainCheckedAt",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DomainConfiguredAt",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DomainStatusDetail",
                table: "Orgs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DomainVercelAddedAt",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DomainVercelTxtHost",
                table: "Orgs",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DomainVercelTxtValue",
                table: "Orgs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DomainVerifiedAt",
                table: "Orgs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PendingDomainRemovals",
                columns: table => new
                {
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingDomainRemovals", x => x.Domain);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingDomainRemovals");

            migrationBuilder.DropColumn(
                name: "DomainCheckFailures",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "DomainCheckedAt",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "DomainConfiguredAt",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "DomainStatusDetail",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "DomainVercelAddedAt",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "DomainVercelTxtHost",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "DomainVercelTxtValue",
                table: "Orgs");

            migrationBuilder.DropColumn(
                name: "DomainVerifiedAt",
                table: "Orgs");
        }
    }
}
