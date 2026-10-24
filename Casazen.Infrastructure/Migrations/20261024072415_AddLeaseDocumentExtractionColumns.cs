using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseDocumentExtractionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocumentHash",
                table: "LeaseDocuments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExtractionCompletedAt",
                table: "LeaseDocuments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtractionError",
                table: "LeaseDocuments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtractionResult",
                table: "LeaseDocuments",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExtractionStartedAt",
                table: "LeaseDocuments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExtractionStatus",
                table: "LeaseDocuments",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentHash",
                table: "LeaseDocuments");

            migrationBuilder.DropColumn(
                name: "ExtractionCompletedAt",
                table: "LeaseDocuments");

            migrationBuilder.DropColumn(
                name: "ExtractionError",
                table: "LeaseDocuments");

            migrationBuilder.DropColumn(
                name: "ExtractionResult",
                table: "LeaseDocuments");

            migrationBuilder.DropColumn(
                name: "ExtractionStartedAt",
                table: "LeaseDocuments");

            migrationBuilder.DropColumn(
                name: "ExtractionStatus",
                table: "LeaseDocuments");
        }
    }
}
