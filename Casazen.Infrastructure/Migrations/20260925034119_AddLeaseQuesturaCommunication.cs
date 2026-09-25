using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseQuesturaCommunication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PropertyDeliveryDate",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QuesturaCommunicationDate",
                table: "LeaseContracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuesturaCommunicationDeclaredByUserId",
                table: "LeaseContracts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuesturaCommunicationReceiptPath",
                table: "LeaseContracts",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PropertyDeliveryDate",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "QuesturaCommunicationDate",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "QuesturaCommunicationDeclaredByUserId",
                table: "LeaseContracts");

            migrationBuilder.DropColumn(
                name: "QuesturaCommunicationReceiptPath",
                table: "LeaseContracts");
        }
    }
}
