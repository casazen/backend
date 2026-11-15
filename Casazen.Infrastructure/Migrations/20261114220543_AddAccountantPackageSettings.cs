using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountantPackageSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountantPackageSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountantEmail = table.Column<string>(type: "text", nullable: true),
                    MonthlySendEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SendDayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    Contents = table.Column<int>(type: "integer", nullable: false),
                    LastSendPeriod = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    LastSendAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSendResult = table.Column<int>(type: "integer", nullable: true),
                    LastSendError = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    StoredPackagePeriod = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    StoredPackageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountantPackageSettings", x => x.Id);
                    table.CheckConstraint("CK_AccountantPackageSettings_Contents", "\"Contents\" BETWEEN 1 AND 255");
                    table.CheckConstraint("CK_AccountantPackageSettings_LastSendResult", "\"LastSendResult\" IS NULL OR \"LastSendResult\" IN (1, 2)");
                    table.CheckConstraint("CK_AccountantPackageSettings_SendDay", "\"SendDayOfMonth\" BETWEEN 1 AND 28");
                    table.ForeignKey(
                        name: "FK_AccountantPackageSettings_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UIX_AccountantPackageSettings_OrgId",
                table: "AccountantPackageSettings",
                column: "OrgId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountantPackageSettings");
        }
    }
}
