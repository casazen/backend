using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyMemberAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResponsibleUserId",
                table: "Properties",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PropertyMemberAccesses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyMemberAccesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyMemberAccesses_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropertyMemberAccesses_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PropertyMemberAccesses_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "RolePermissions",
                columns: new[] { "PermissionKey", "RoleId" },
                values: new object[,]
                {
                    { "alloggiati.submit", 1 },
                    { "guest.manage", 1 },
                    { "servicerequest.write", 1 },
                    { "alloggiati.submit", 7 },
                    { "guest.manage", 7 },
                    { "servicerequest.write", 7 },
                    { "servicerequest.write", 9 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Properties_ResponsibleUserId",
                table: "Properties",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyMemberAccesses_OrgId_UserId",
                table: "PropertyMemberAccesses",
                columns: new[] { "OrgId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_PropertyMemberAccesses_PropertyId",
                table: "PropertyMemberAccesses",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_PropertyMemberAccesses_UserId_PropertyId",
                table: "PropertyMemberAccesses",
                columns: new[] { "UserId", "PropertyId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Properties_Users_ResponsibleUserId",
                table: "Properties",
                column: "ResponsibleUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Properties_Users_ResponsibleUserId",
                table: "Properties");

            migrationBuilder.DropTable(
                name: "PropertyMemberAccesses");

            migrationBuilder.DropIndex(
                name: "IX_Properties_ResponsibleUserId",
                table: "Properties");

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "alloggiati.submit", 1 });

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "guest.manage", 1 });

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "servicerequest.write", 1 });

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "alloggiati.submit", 7 });

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "guest.manage", 7 });

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "servicerequest.write", 7 });

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "servicerequest.write", 9 });

            migrationBuilder.DropColumn(
                name: "ResponsibleUserId",
                table: "Properties");
        }
    }
}
