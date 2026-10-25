using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestCrmProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GuestCrmProfileId",
                table: "Guests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GuestCrmProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompositeKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConfirmedMarketingEmail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MarketingConsentEmail = table.Column<bool>(type: "boolean", nullable: false),
                    MarketingConsentWhatsApp = table.Column<bool>(type: "boolean", nullable: false),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TotalStays = table.Column<int>(type: "integer", nullable: false),
                    TotalSpend = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PropertyIdsJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    TagsJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    FirstStayAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastStayAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuestCrmProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuestCrmProfiles_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Guests_GuestCrmProfileId",
                table: "Guests",
                column: "GuestCrmProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_GuestCrmProfiles_OrgId",
                table: "GuestCrmProfiles",
                column: "OrgId");

            migrationBuilder.AddForeignKey(
                name: "FK_Guests_GuestCrmProfiles_GuestCrmProfileId",
                table: "Guests",
                column: "GuestCrmProfileId",
                principalTable: "GuestCrmProfiles",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Guests_GuestCrmProfiles_GuestCrmProfileId",
                table: "Guests");

            migrationBuilder.DropTable(
                name: "GuestCrmProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Guests_GuestCrmProfileId",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "GuestCrmProfileId",
                table: "Guests");
        }
    }
}
