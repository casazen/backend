using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LeasePartyRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataRetentionUntil",
                table: "LeaseContracts");

            migrationBuilder.AddColumn<DateTime>(
                name: "AnonymizedAt",
                table: "Parties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ErasureRequestedAt",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PartiesAnonymizedAt",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnonymizedAt",
                table: "Parties");

            migrationBuilder.DropColumn(
                name: "ErasureRequestedAt",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "PartiesAnonymizedAt",
                table: "LeaseContracts");

            migrationBuilder.AddColumn<DateTime>(
                name: "DataRetentionUntil",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // The value the column held before LT-12: start date + 10 years.
            migrationBuilder.Sql("""UPDATE "LeaseContracts" SET "DataRetentionUntil" = "StartDate" + interval '10 years';""");
        }
    }
}
