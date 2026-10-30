using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceRequestBackupGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BackupAcceptedAt",
                table: "ServiceRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackupAcceptedByUserId",
                table: "ServiceRequests",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "ServiceRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroupRole",
                table: "ServiceRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SupplierBackupsEnabled",
                table: "Orgs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "ServiceRequestGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phase = table.Column<int>(type: "integer", nullable: false),
                    PrimaryDueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BackupDueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SlotPlanned = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceRequestGroups", x => x.Id);
                    table.CheckConstraint("CK_ServiceRequestGroups_Phase", "\"Phase\" IN (0, 1, 2)");
                    table.CheckConstraint("CK_ServiceRequestGroups_Windows", "(\"Phase\" = 0 AND \"BackupDueAt\" IS NULL AND \"ClosedAt\" IS NULL) OR (\"Phase\" = 1 AND \"BackupDueAt\" IS NOT NULL AND \"ClosedAt\" IS NULL) OR (\"Phase\" = 2 AND \"ClosedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ServiceRequestGroups_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_GroupId",
                table: "ServiceRequests",
                column: "GroupId",
                filter: "\"GroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_ServiceRequests_GroupId_Primary",
                table: "ServiceRequests",
                column: "GroupId",
                unique: true,
                filter: "\"GroupRole\" = 1");

            migrationBuilder.CreateIndex(
                name: "UIX_ServiceRequests_GroupId_Promoted",
                table: "ServiceRequests",
                column: "GroupId",
                unique: true,
                filter: "\"GroupRole\" = 4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ServiceRequests_GroupRole",
                table: "ServiceRequests",
                sql: "(\"GroupId\" IS NULL AND \"GroupRole\" = 0 AND \"BackupAcceptedAt\" IS NULL AND \"BackupAcceptedByUserId\" IS NULL) OR (\"GroupId\" IS NOT NULL AND \"GroupRole\" BETWEEN 1 AND 4)");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequestGroups_Open",
                table: "ServiceRequestGroups",
                columns: new[] { "Phase", "PrimaryDueAt" },
                filter: "\"Phase\" <> 2");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequestGroups_OrgId",
                table: "ServiceRequestGroups",
                column: "OrgId");

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceRequests_ServiceRequestGroups_GroupId",
                table: "ServiceRequests",
                column: "GroupId",
                principalTable: "ServiceRequestGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ServiceRequests_ServiceRequestGroups_GroupId",
                table: "ServiceRequests");

            migrationBuilder.DropTable(
                name: "ServiceRequestGroups");

            migrationBuilder.DropIndex(
                name: "IX_ServiceRequests_GroupId",
                table: "ServiceRequests");

            migrationBuilder.DropIndex(
                name: "UIX_ServiceRequests_GroupId_Primary",
                table: "ServiceRequests");

            migrationBuilder.DropIndex(
                name: "UIX_ServiceRequests_GroupId_Promoted",
                table: "ServiceRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ServiceRequests_GroupRole",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "BackupAcceptedAt",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "BackupAcceptedByUserId",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "GroupRole",
                table: "ServiceRequests");

            migrationBuilder.DropColumn(
                name: "SupplierBackupsEnabled",
                table: "Orgs");
        }
    }
}
