using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerStatementsAndInvitations : Migration
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
                name: "cf_simulated_practices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PracticeId = table.Column<string>(type: "character varying(18)", maxLength: 18, nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Comune = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Provincia = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CloseReason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StatusChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ControlTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cf_simulated_practices", x => x.Id);
                },
                comment: "solo per ambienti di prova: pratiche del modulo simulato di ConcordatoFacile (ConcordatoFacile:FormMode=Simulated); vuota e inerte con FormMode=Real, la sola modalita di produzione");

            migrationBuilder.CreateTable(
                name: "InternalActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    StaffUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ActivityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    InternalRate = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    RechargeToOwner = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InternalActivities_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_InternalActivities_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InternalActivities_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateIndex(
                name: "IX_cf_simulated_practices_LeaseId_SubmittedAt",
                table: "cf_simulated_practices",
                columns: new[] { "LeaseId", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_cf_simulated_practices_PracticeId",
                table: "cf_simulated_practices",
                column: "PracticeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalActivities_BookingId",
                table: "InternalActivities",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalActivities_OrgId",
                table: "InternalActivities",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalActivities_PropertyId",
                table: "InternalActivities",
                column: "PropertyId");

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
                name: "cf_simulated_practices");

            migrationBuilder.DropTable(
                name: "InternalActivities");

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
