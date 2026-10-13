using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLtrReferenceDataAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "TerritorialRentAgreements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpiryNote",
                table: "TerritorialRentAgreements",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RemainsInForceUntilReplaced",
                table: "TerritorialRentAgreements",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "TerritorialRentAgreements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationSource",
                table: "TerritorialRentAgreements",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ComuneImuChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Comune = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RecipientOffice = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Email = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Pec = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PostalAddress = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Instructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RatePercent = table.Column<decimal>(type: "numeric(7,3)", precision: 7, scale: 3, nullable: true),
                    EffectiveRatePercent = table.Column<decimal>(type: "numeric(7,3)", precision: 7, scale: 3, nullable: true),
                    RateYear = table.Column<int>(type: "integer", nullable: true),
                    RateKind = table.Column<int>(type: "integer", nullable: true),
                    RateNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RateSourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DataCompleteness = table.Column<int>(type: "integer", nullable: false),
                    LastVerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VerificationSource = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComuneImuChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegulatoryDataAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    ChangedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Changes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegulatoryDataAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComuneImuChannels_Comune",
                table: "ComuneImuChannels",
                column: "Comune");

            migrationBuilder.CreateIndex(
                name: "IX_RegulatoryDataAuditEntries_EntityId_OccurredAt",
                table: "RegulatoryDataAuditEntries",
                columns: new[] { "EntityId", "OccurredAt" });

            ApplyData(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RevertData(migrationBuilder);

            migrationBuilder.DropTable(
                name: "ComuneImuChannels");

            migrationBuilder.DropTable(
                name: "RegulatoryDataAuditEntries");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "ExpiryNote",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "RemainsInForceUntilReplaced",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "TerritorialRentAgreements");

            migrationBuilder.DropColumn(
                name: "VerificationSource",
                table: "TerritorialRentAgreements");
        }
    }
}
