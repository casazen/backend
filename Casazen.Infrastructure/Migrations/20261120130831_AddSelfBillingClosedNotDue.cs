using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfBillingClosedNotDue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedNotDueAt",
                table: "FiscalDocuments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedNotDueByUserId",
                table: "FiscalDocuments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedNotDueNote",
                table: "FiscalDocuments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedNotDueReason",
                table: "FiscalDocuments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_FiscalDocuments_ClosedNotDue",
                table: "FiscalDocuments",
                sql: "(\"ClosedNotDueAt\" IS NULL AND \"ClosedNotDueReason\" IS NULL AND \"ClosedNotDueByUserId\" IS NULL AND \"ClosedNotDueNote\" IS NULL) OR (\"ClosedNotDueAt\" IS NOT NULL AND \"ClosedNotDueReason\" IS NOT NULL AND \"SequenceNumber\" IS NOT NULL AND \"Status\" = 3)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FiscalDocuments_ClosedNotDue",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "ClosedNotDueAt",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "ClosedNotDueByUserId",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "ClosedNotDueNote",
                table: "FiscalDocuments");

            migrationBuilder.DropColumn(
                name: "ClosedNotDueReason",
                table: "FiscalDocuments");
        }
    }
}
