using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSignupAttributions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignupAttributions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    UtmSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UtmMedium = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UtmCampaign = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UtmTerm = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UtmContent = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ComuneCode = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: true),
                    LandingPath = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReferrerHost = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignupAttributions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignupAttributions_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignupAttributions_RecordedAt",
                table: "SignupAttributions",
                column: "RecordedAt");

            migrationBuilder.CreateIndex(
                name: "UIX_SignupAttributions_OrgId",
                table: "SignupAttributions",
                column: "OrgId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignupAttributions");
        }
    }
}
