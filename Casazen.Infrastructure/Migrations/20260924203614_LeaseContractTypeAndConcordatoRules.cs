using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LeaseContractTypeAndConcordatoRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AirConditioningUpliftPercent",
                table: "TerritorialRentAgreements",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "BalconyAppurtenancePercent",
                table: "TerritorialRentAgreements",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "CoefficientCombination",
                table: "TerritorialRentAgreements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "GarageAppurtenancePercent",
                table: "TerritorialRentAgreements",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "GreenAreaAppurtenancePercent",
                table: "TerritorialRentAgreements",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OtherAppurtenancePercent",
                table: "TerritorialRentAgreements",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "StoveHeatingMinTypeBCount",
                table: "TerritorialRentAgreements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SubFascia2MinTypeBCount",
                table: "TerritorialRentAgreements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SubFascia3MaxMinTypeDCount",
                table: "TerritorialRentAgreements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SubFascia3MinQualifyingTypeDCount",
                table: "TerritorialRentAgreements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SubFascia3MinTypeCCount",
                table: "TerritorialRentAgreements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SubFascia3QualifyingTypeDElements",
                table: "TerritorialRentAgreements",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApeCode",
                table: "PropertyDocuments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApeEnergyClass",
                table: "PropertyDocuments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CadastralCategory",
                table: "Properties",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CadastralIncome",
                table: "Properties",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CadastralParcel",
                table: "Properties",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CadastralSheet",
                table: "Properties",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CadastralSubaltern",
                table: "Properties",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConcordatoAssessment_AirConditioning",
                table: "LeaseContracts",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConcordatoAssessment_BalconySqm",
                table: "LeaseContracts",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConcordatoAssessment_CadastralSheet",
                table: "LeaseContracts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConcordatoAssessment_CalculatedAt",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConcordatoAssessment_CanoneMaxAnnuo",
                table: "LeaseContracts",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConcordatoAssessment_CanoneMaxMensile",
                table: "LeaseContracts",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConcordatoAssessment_CanoneMinAnnuo",
                table: "LeaseContracts",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConcordatoAssessment_CanoneMinMensile",
                table: "LeaseContracts",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConcordatoAssessment_ContractYears",
                table: "LeaseContracts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConcordatoAssessment_DataCompleteness",
                table: "LeaseContracts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConcordatoAssessment_GarageSqm",
                table: "LeaseContracts",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConcordatoAssessment_IsFurnished",
                table: "LeaseContracts",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConcordatoAssessment_OtherAppurtenanceSqm",
                table: "LeaseContracts",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConcordatoAssessment_PrivateGreenSqm",
                table: "LeaseContracts",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConcordatoAssessment_QualifyingTypeDElementCount",
                table: "LeaseContracts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConcordatoAssessment_RentWithinRange",
                table: "LeaseContracts",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConcordatoAssessment_Sqm",
                table: "LeaseContracts",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConcordatoAssessment_StoveHeating",
                table: "LeaseContracts",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConcordatoAssessment_SubFascia",
                table: "LeaseContracts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConcordatoAssessment_TypeAElementCount",
                table: "LeaseContracts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConcordatoAssessment_TypeBElementCount",
                table: "LeaseContracts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConcordatoAssessment_TypeCElementCount",
                table: "LeaseContracts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConcordatoAssessment_TypeDElementCount",
                table: "LeaseContracts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConcordatoAssessment_UsableSqm",
                table: "LeaseContracts",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConcordatoAssessment_Zone",
                table: "LeaseContracts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConcordatoAssessment_ZoneName",
                table: "LeaseContracts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContractType",
                table: "LeaseContracts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "SecurityDeposit",
                table: "LeaseContracts",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TaxRegime",
                table: "LeaseContracts",
                type: "integer",
                nullable: true);

            ApplyData(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RevertData(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "AirConditioningUpliftPercent",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "BalconyAppurtenancePercent",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "CoefficientCombination",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "GarageAppurtenancePercent",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "GreenAreaAppurtenancePercent",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "OtherAppurtenancePercent",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "StoveHeatingMinTypeBCount",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "SubFascia2MinTypeBCount",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "SubFascia3MaxMinTypeDCount",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "SubFascia3MinQualifyingTypeDCount",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "SubFascia3MinTypeCCount",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "SubFascia3QualifyingTypeDElements",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "ApeCode",
                table: "PropertyDocuments");

            migrationBuilder.DropColumn(
                name: "ApeEnergyClass",
                table: "PropertyDocuments");

            migrationBuilder.DropColumn(
                name: "CadastralCategory",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "CadastralIncome",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "CadastralParcel",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "CadastralSheet",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "CadastralSubaltern",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_AirConditioning",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_BalconySqm",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_CadastralSheet",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_CalculatedAt",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_CanoneMaxAnnuo",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_CanoneMaxMensile",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_CanoneMinAnnuo",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_CanoneMinMensile",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_ContractYears",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_DataCompleteness",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_GarageSqm",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_IsFurnished",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_OtherAppurtenanceSqm",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_PrivateGreenSqm",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_QualifyingTypeDElementCount",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_RentWithinRange",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_Sqm",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_StoveHeating",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_SubFascia",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_TypeAElementCount",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_TypeBElementCount",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_TypeCElementCount",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_TypeDElementCount",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_UsableSqm",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_Zone",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ConcordatoAssessment_ZoneName",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "ContractType",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "SecurityDeposit",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "TaxRegime",
                table: "LeaseContracts");
        }
    }
}
