using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerTaxChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExternalShortLetUnits",
                table: "PropertyOwners",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CoOwnerTaxReporting",
                table: "OwnerMandates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ShortLetTaxDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Regime = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShortLetTaxDecisions", x => x.Id);
                    table.CheckConstraint("CK_ShortLetTaxDecisions_Regime", "\"Regime\" IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_ShortLetTaxDecisions_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PropertyOwners_ExternalShortLetUnits",
                table: "PropertyOwners",
                sql: "\"ExternalShortLetUnits\" >= 0 AND \"ExternalShortLetUnits\" <= 1000");

            migrationBuilder.CreateIndex(
                name: "IX_ShortLetTaxDecisions_OrgId_BookingId",
                table: "ShortLetTaxDecisions",
                columns: new[] { "OrgId", "BookingId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShortLetTaxDecisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PropertyOwners_ExternalShortLetUnits",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "ExternalShortLetUnits",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "CoOwnerTaxReporting",
                table: "OwnerMandates");
        }
    }
}
