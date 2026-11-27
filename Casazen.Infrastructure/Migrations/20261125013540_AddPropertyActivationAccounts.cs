using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyActivationAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_OnboardingAssistRequests_OpenPerPartPropertyAndChannel",
                table: "OnboardingAssistRequests");

            migrationBuilder.CreateTable(
                name: "PropertyActivationCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    Service = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    Password = table.Column<string>(type: "text", nullable: false),
                    WebServiceKey = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyActivationCredentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PropertyActivationCredentialAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Service = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    UnitCode = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyActivationCredentialAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyActivationCredentialAssignments_Properties_Property~",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PropertyActivationCredentialAssignments_PropertyActivationC~",
                        column: x => x.AccountId,
                        principalTable: "PropertyActivationCredentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UIX_OnboardingAssistRequests_OpenPerPartPropertyAndChannel",
                table: "OnboardingAssistRequests",
                columns: new[] { "OrgId", "PartKey", "PropertyId", "ChannelCode", "Context" },
                unique: true,
                filter: "\"Status\" IN (1, 2)")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyActivationCredentialAssignments_AccountId",
                table: "PropertyActivationCredentialAssignments",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyActivationCredentialAssignments_OrgId_PropertyId_Se~",
                table: "PropertyActivationCredentialAssignments",
                columns: new[] { "OrgId", "PropertyId", "Service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyActivationCredentialAssignments_PropertyId",
                table: "PropertyActivationCredentialAssignments",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyActivationCredentials_OrgId_ExpiresAt",
                table: "PropertyActivationCredentials",
                columns: new[] { "OrgId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyActivationCredentialAssignments");

            migrationBuilder.DropTable(
                name: "PropertyActivationCredentials");

            migrationBuilder.DropIndex(
                name: "UIX_OnboardingAssistRequests_OpenPerPartPropertyAndChannel",
                table: "OnboardingAssistRequests");

            migrationBuilder.CreateIndex(
                name: "UIX_OnboardingAssistRequests_OpenPerPartPropertyAndChannel",
                table: "OnboardingAssistRequests",
                columns: new[] { "OrgId", "PartKey", "PropertyId", "ChannelCode" },
                unique: true,
                filter: "\"Status\" IN (1, 2)")
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
