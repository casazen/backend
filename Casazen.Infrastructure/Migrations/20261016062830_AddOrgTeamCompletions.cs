using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrgTeamCompletions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSentAt",
                table: "OrgInvitations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<List<DateTime>>(
                name: "SendLog",
                table: "OrgInvitations",
                type: "timestamp with time zone[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.CreateTable(
                name: "OrgInvitationProperties",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvitationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgInvitationProperties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrgInvitationProperties_OrgInvitations_InvitationId",
                        column: x => x.InvitationId,
                        principalTable: "OrgInvitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrgInvitationProperties_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrgInvitationProperties_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Roles",
                columns: new[] { "Id", "ContextKey", "RoleKey" },
                values: new object[] { 13, "account", "org_manager" });

            migrationBuilder.InsertData(
                table: "RolePermissions",
                columns: new[] { "PermissionKey", "RoleId" },
                values: new object[] { "org.suppliers.manage", 13 });

            migrationBuilder.CreateIndex(
                name: "IX_OrgInvitationProperties_OrgId",
                table: "OrgInvitationProperties",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgInvitationProperties_PropertyId",
                table: "OrgInvitationProperties",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "UIX_OrgInvitationProperties_InvitationId_PropertyId",
                table: "OrgInvitationProperties",
                columns: new[] { "InvitationId", "PropertyId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // AM-02c: the memberships of the role this migration seeds (account/org_manager, id 13) go first: a property manager
            // added since holds one, and the foreign key would refuse to delete the role under it (same rule as AddOrgMembership).
            migrationBuilder.Sql("DELETE FROM \"UserContextMemberships\" WHERE \"RoleId\" = 13;");

            migrationBuilder.DropTable(
                name: "OrgInvitationProperties");

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: new[] { "PermissionKey", "RoleId" },
                keyValues: new object[] { "org.suppliers.manage", 13 });

            migrationBuilder.DeleteData(
                table: "Roles",
                keyColumn: "Id",
                keyValue: 13);

            migrationBuilder.DropColumn(
                name: "LastSentAt",
                table: "OrgInvitations");

            migrationBuilder.DropColumn(
                name: "SendLog",
                table: "OrgInvitations");
        }
    }
}
