using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseRegistrationAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LeaseRegistrationAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorizerUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AuthorizedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TosVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    AttestationAccepted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseRegistrationAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeaseRegistrationAuthorizations_LeaseContracts_LeaseContractId",
                        column: x => x.LeaseContractId,
                        principalTable: "LeaseContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeaseRegistrationAuthorizations_Orgs_OrgId",
                        column: x => x.OrgId,
                        principalTable: "Orgs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseRegistrationAuthorizations_LeaseContractId",
                table: "LeaseRegistrationAuthorizations",
                column: "LeaseContractId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseRegistrationAuthorizations_OrgId",
                table: "LeaseRegistrationAuthorizations",
                column: "OrgId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeaseRegistrationAuthorizations");
        }
    }
}
