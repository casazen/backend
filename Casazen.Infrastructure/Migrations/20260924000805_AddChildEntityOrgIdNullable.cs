using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChildEntityOrgIdNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "PropertyDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "PricingHistories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "PricingAdapterConfigs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "OtaIntegrations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "AlloggiatiWebReports",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyDocuments_OrgId",
                table: "PropertyDocuments",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PricingHistories_OrgId",
                table: "PricingHistories",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_PricingAdapterConfigs_OrgId",
                table: "PricingAdapterConfigs",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_OtaIntegrations_OrgId",
                table: "OtaIntegrations",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "IX_AlloggiatiWebReports_OrgId",
                table: "AlloggiatiWebReports",
                column: "OrgId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PropertyDocuments_OrgId",
                table: "PropertyDocuments");

            migrationBuilder.DropIndex(
                name: "IX_PricingHistories_OrgId",
                table: "PricingHistories");

            migrationBuilder.DropIndex(
                name: "IX_PricingAdapterConfigs_OrgId",
                table: "PricingAdapterConfigs");

            migrationBuilder.DropIndex(
                name: "IX_OtaIntegrations_OrgId",
                table: "OtaIntegrations");

            migrationBuilder.DropIndex(
                name: "IX_AlloggiatiWebReports_OrgId",
                table: "AlloggiatiWebReports");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "PropertyDocuments");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "PricingHistories");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "PricingAdapterConfigs");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "OtaIntegrations");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "AlloggiatiWebReports");
        }
    }
}
