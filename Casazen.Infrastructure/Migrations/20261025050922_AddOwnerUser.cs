using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "PropertyOwnerships",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OwnerUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Auth0Sub = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerUsers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyOwnerships_OwnerUserId",
                table: "PropertyOwnerships",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_PropertyOwnerships_OwnerUsers_OwnerUserId",
                table: "PropertyOwnerships",
                column: "OwnerUserId",
                principalTable: "OwnerUsers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PropertyOwnerships_OwnerUsers_OwnerUserId",
                table: "PropertyOwnerships");

            migrationBuilder.DropTable(
                name: "OwnerUsers");

            migrationBuilder.DropIndex(
                name: "IX_PropertyOwnerships_OwnerUserId",
                table: "PropertyOwnerships");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "PropertyOwnerships");
        }
    }
}
