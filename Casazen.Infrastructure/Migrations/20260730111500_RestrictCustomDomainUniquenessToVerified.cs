using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260730111500_RestrictCustomDomainUniquenessToVerified")]
    public partial class RestrictCustomDomainUniquenessToVerified : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orgs_CustomDomain",
                table: "Orgs");

            migrationBuilder.CreateIndex(
                name: "IX_Orgs_CustomDomain",
                table: "Orgs",
                column: "CustomDomain",
                unique: true,
                filter: "\"CustomDomain\" IS NOT NULL AND \"DomainVerificationStatus\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orgs_CustomDomain",
                table: "Orgs");

            migrationBuilder.CreateIndex(
                name: "IX_Orgs_CustomDomain",
                table: "Orgs",
                column: "CustomDomain",
                unique: true,
                filter: "\"CustomDomain\" IS NOT NULL");
        }
    }
}
