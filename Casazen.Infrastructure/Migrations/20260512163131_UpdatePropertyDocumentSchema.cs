using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdatePropertyDocumentSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "PropertyDocuments");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "PropertyDocuments",
                newName: "UploadedAt");

            migrationBuilder.RenameColumn(
                name: "FileUrl",
                table: "PropertyDocuments",
                newName: "StorageUrl");

            migrationBuilder.AddColumn<string>(
                name: "UploadedBy",
                table: "PropertyDocuments",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UploadedBy",
                table: "PropertyDocuments");

            migrationBuilder.RenameColumn(
                name: "UploadedAt",
                table: "PropertyDocuments",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "StorageUrl",
                table: "PropertyDocuments",
                newName: "FileUrl");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "PropertyDocuments",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }
    }
}
