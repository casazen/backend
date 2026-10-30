using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertySiteProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AreaKind",
                table: "Properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "CheckInTo",
                table: "Properties",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ChildrenWelcome",
                table: "Properties",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnergyClass",
                table: "Properties",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EnergyIndex",
                table: "Properties",
                type: "numeric(7,2)",
                precision: 7,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FloorLevel",
                table: "Properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoAltJson",
                table: "Properties",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RenovatedYear",
                table: "Properties",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SiteProfileJson",
                table: "Properties",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TourUrl",
                table: "Properties",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoUrl",
                table: "Properties",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Properties_EnergyClass",
                table: "Properties",
                sql: "\"EnergyClass\" IS NULL OR \"EnergyClass\" IN ('A4', 'A3', 'A2', 'A1', 'A', 'B', 'C', 'D', 'E', 'F', 'G')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Properties_EnergyIndex",
                table: "Properties",
                sql: "\"EnergyIndex\" IS NULL OR (\"EnergyClass\" IS NOT NULL AND \"EnergyIndex\" >= 0 AND \"EnergyIndex\" <= 9999)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Properties_FloorLevel",
                table: "Properties",
                sql: "\"FloorLevel\" IS NULL OR \"FloorLevel\" BETWEEN -3 AND 60");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Properties_RenovatedYear",
                table: "Properties",
                sql: "\"RenovatedYear\" IS NULL OR \"RenovatedYear\" >= 1800");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Properties_EnergyClass",
                table: "Properties");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Properties_EnergyIndex",
                table: "Properties");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Properties_FloorLevel",
                table: "Properties");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Properties_RenovatedYear",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "AreaKind",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "CheckInTo",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "ChildrenWelcome",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "EnergyClass",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "EnergyIndex",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "FloorLevel",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "PhotoAltJson",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "RenovatedYear",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "SiteProfileJson",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "TourUrl",
                table: "Properties");

            migrationBuilder.DropColumn(
                name: "VideoUrl",
                table: "Properties");
        }
    }
}
