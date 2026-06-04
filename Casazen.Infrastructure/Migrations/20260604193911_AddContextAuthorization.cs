using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContextAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastUsedContextKey",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppContexts",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppContexts", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContextKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RoleKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Roles_AppContexts_ContextKey",
                        column: x => x.ContextKey,
                        principalTable: "AppContexts",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    RoleId = table.Column<int>(type: "integer", nullable: false),
                    PermissionKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.PermissionKey });
                    table.ForeignKey(
                        name: "FK_RolePermissions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserContextMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContextKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RoleId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserContextMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserContextMemberships_AppContexts_ContextKey",
                        column: x => x.ContextKey,
                        principalTable: "AppContexts",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserContextMemberships_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserContextMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AppContexts",
                columns: new[] { "Key", "DisplayName" },
                values: new object[,]
                {
                    { "admin", "Amministrazione" },
                    { "long-rent", "Affitti lungo termine" },
                    { "short-rent", "Affitti brevi" }
                });

            migrationBuilder.InsertData(
                table: "Roles",
                columns: new[] { "Id", "ContextKey", "RoleKey" },
                values: new object[,]
                {
                    { 1, "short-rent", "property_owner" },
                    { 2, "long-rent", "long_term_landlord" },
                    { 3, "admin", "platform_admin" }
                });

            migrationBuilder.InsertData(
                table: "RolePermissions",
                columns: new[] { "PermissionKey", "RoleId" },
                values: new object[,]
                {
                    { "booking.read", 1 },
                    { "booking.write", 1 },
                    { "guest.read", 1 },
                    { "guest.write", 1 },
                    { "ota.read", 1 },
                    { "ota.write", 1 },
                    { "payment.read", 1 },
                    { "payment.write", 1 },
                    { "property.read", 1 },
                    { "property.write", 1 },
                    { "lease.create", 2 },
                    { "lease.read", 2 },
                    { "lease.register", 2 },
                    { "lease.sign", 2 },
                    { "admin.cin.read", 3 },
                    { "admin.jobs.read", 3 },
                    { "admin.stats.read", 3 },
                    { "admin.tax.manage", 3 },
                    { "admin.users.manage", 3 },
                    { "admin.users.read", 3 }
                });

            migrationBuilder.Sql("""
                INSERT INTO "UserContextMemberships" ("Id", "UserId", "ContextKey", "RoleId")
                SELECT
                    ('00000000-0000-0000-0000-' || substring(md5(u."Id" || ':short-rent') from 1 for 12))::uuid,
                    u."Id",
                    'short-rent',
                    1
                FROM "Users" u
                WHERE u."Role" = 1
                ON CONFLICT ("UserId", "ContextKey") DO NOTHING;

                INSERT INTO "UserContextMemberships" ("Id", "UserId", "ContextKey", "RoleId")
                SELECT
                    ('00000000-0000-0000-0000-' || substring(md5(u."Id" || ':long-rent') from 1 for 12))::uuid,
                    u."Id",
                    'long-rent',
                    2
                FROM "Users" u
                WHERE u."Role" = 5
                ON CONFLICT ("UserId", "ContextKey") DO NOTHING;

                INSERT INTO "UserContextMemberships" ("Id", "UserId", "ContextKey", "RoleId")
                SELECT
                    ('00000000-0000-0000-0000-' || substring(md5(u."Id" || ':admin') from 1 for 12))::uuid,
                    u."Id",
                    'admin',
                    3
                FROM "Users" u
                WHERE u."Role" = 0
                ON CONFLICT ("UserId", "ContextKey") DO NOTHING;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_ContextKey_RoleKey",
                table: "Roles",
                columns: new[] { "ContextKey", "RoleKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserContextMemberships_ContextKey",
                table: "UserContextMemberships",
                column: "ContextKey");

            migrationBuilder.CreateIndex(
                name: "IX_UserContextMemberships_RoleId",
                table: "UserContextMemberships",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserContextMemberships_UserId_ContextKey",
                table: "UserContextMemberships",
                columns: new[] { "UserId", "ContextKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "UserContextMemberships");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "AppContexts");

            migrationBuilder.DropColumn(
                name: "LastUsedContextKey",
                table: "Users");
        }
    }
}
