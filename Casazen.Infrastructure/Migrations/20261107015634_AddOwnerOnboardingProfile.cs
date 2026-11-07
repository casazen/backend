using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerOnboardingProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Bic",
                table: "PropertyOwners",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingCity",
                table: "PropertyOwners",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingCivic",
                table: "PropertyOwners",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingCountry",
                table: "PropertyOwners",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingPostalCode",
                table: "PropertyOwners",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingProvince",
                table: "PropertyOwners",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingStreet",
                table: "PropertyOwners",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EntityKind",
                table: "PropertyOwners",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalKey",
                table: "PropertyOwners",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalName",
                table: "PropertyOwners",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SettlementCurrency",
                table: "PropertyOwners",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxResidenceCountry",
                table: "PropertyOwners",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VatNumber",
                table: "PropertyOwners",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UIX_PropertyOwners_OrgId_ExternalKey",
                table: "PropertyOwners",
                columns: new[] { "OrgId", "ExternalKey" },
                unique: true,
                filter: "\"ExternalKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_PropertyOwners_OrgId_ExternalKey",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "Bic",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "BillingCity",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "BillingCivic",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "BillingCountry",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "BillingPostalCode",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "BillingProvince",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "BillingStreet",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "EntityKind",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "ExternalKey",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "LegalName",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "SettlementCurrency",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "TaxResidenceCountry",
                table: "PropertyOwners");

            migrationBuilder.DropColumn(
                name: "VatNumber",
                table: "PropertyOwners");
        }
    }
}
