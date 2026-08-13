using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260725110112_NormalizeOrgPublicHostState")]
    public partial class NormalizeOrgPublicHostState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "Orgs"
                SET "PublicHostMode" = 1
                WHERE "PublicHostMode" = 0
                  AND "Subdomain" IS NULL
                  AND "CustomDomain" IS NULL;
                """);

            migrationBuilder.Sql(
                """
                UPDATE "Orgs"
                SET "Subdomain" = NULL
                WHERE "PublicHostMode" <> 0
                  AND "Subdomain" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
