using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSeoEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SeoEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Event = table.Column<int>(type: "integer", nullable: false),
                    ComuneCode = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    UtmSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UtmMedium = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UtmCampaign = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReferrerHost = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeoEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeoEvents_ComuneCode_OccurredAt",
                table: "SeoEvents",
                columns: new[] { "ComuneCode", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SeoEvents_OccurredAt",
                table: "SeoEvents",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeoEvents");
        }
    }
}
