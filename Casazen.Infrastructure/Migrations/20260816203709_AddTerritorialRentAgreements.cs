using System;
using Casazen.Infrastructure.Data.Seeds;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTerritorialRentAgreements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HighTensionAreaComuni",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Comune = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    VerifiedDirectly = table.Column<bool>(type: "boolean", nullable: false),
                    LastVerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HighTensionAreaComuni", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TerritorialRentAgreements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Comune = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AgreementName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SignedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DataCompleteness = table.Column<int>(type: "integer", nullable: false),
                    LastVerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RequiredTypeACount = table.Column<int>(type: "integer", nullable: false),
                    FurnishedUpliftPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    SmallSqmMax = table.Column<int>(type: "integer", nullable: false),
                    SmallSqmUpliftPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    MidSqmMin = table.Column<int>(type: "integer", nullable: false),
                    MidSqmMax = table.Column<int>(type: "integer", nullable: false),
                    MidSqmUpliftPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    LargeSqmMin = table.Column<int>(type: "integer", nullable: false),
                    LargeSqmReductionPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    Duration4UpliftPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    Duration5UpliftPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    Duration6UpliftPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerritorialRentAgreements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConcordatoRentBands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TerritorialRentAgreementId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZoneName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CadastralSheets = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MinSqm = table.Column<int>(type: "integer", nullable: false),
                    MaxSqm = table.Column<int>(type: "integer", nullable: true),
                    SubFascia1MinEurSqmYear = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SubFascia1MaxEurSqmYear = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SubFascia2MinEurSqmYear = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SubFascia2MaxEurSqmYear = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SubFascia3MinEurSqmYear = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SubFascia3MaxEurSqmYear = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConcordatoRentBands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConcordatoRentBands_TerritorialRentAgreements_TerritorialRe~",
                        column: x => x.TerritorialRentAgreementId,
                        principalTable: "TerritorialRentAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TerritorialAgreementSignatories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TerritorialRentAgreementId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Contact = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerritorialAgreementSignatories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TerritorialAgreementSignatories_TerritorialRentAgreements_T~",
                        column: x => x.TerritorialRentAgreementId,
                        principalTable: "TerritorialRentAgreements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConcordatoRentBands_TerritorialRentAgreementId",
                table: "ConcordatoRentBands",
                column: "TerritorialRentAgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_HighTensionAreaComuni_Comune",
                table: "HighTensionAreaComuni",
                column: "Comune");

            migrationBuilder.CreateIndex(
                name: "IX_TerritorialAgreementSignatories_TerritorialRentAgreementId",
                table: "TerritorialAgreementSignatories",
                column: "TerritorialRentAgreementId");

            migrationBuilder.CreateIndex(
                name: "IX_TerritorialRentAgreements_Comune",
                table: "TerritorialRentAgreements",
                column: "Comune");

            SeedMonzaBrianza(migrationBuilder);
        }

        private static void SeedMonzaBrianza(MigrationBuilder migrationBuilder)
        {
            foreach (var agreement in CanoneConcordatoMbSeed.BuildAgreements())
            {
                migrationBuilder.InsertData(
                    table: "TerritorialRentAgreements",
                    columns: new[]
                    {
                        "Id", "Comune", "Region", "AgreementName", "SignedDate", "EffectiveDate",
                        "SourceUrl", "DataCompleteness", "LastVerifiedAt", "RequiredTypeACount",
                        "FurnishedUpliftPercent", "SmallSqmMax", "SmallSqmUpliftPercent",
                        "MidSqmMin", "MidSqmMax", "MidSqmUpliftPercent",
                        "LargeSqmMin", "LargeSqmReductionPercent",
                        "Duration4UpliftPercent", "Duration5UpliftPercent", "Duration6UpliftPercent",
                    },
                    values: new object[]
                    {
                        agreement.Id, agreement.Comune, agreement.Region, agreement.AgreementName,
                        agreement.SignedDate, agreement.EffectiveDate, agreement.SourceUrl,
                        (int)agreement.DataCompleteness, agreement.LastVerifiedAt, agreement.RequiredTypeACount,
                        agreement.FurnishedUpliftPercent, agreement.SmallSqmMax, agreement.SmallSqmUpliftPercent,
                        agreement.MidSqmMin, agreement.MidSqmMax, agreement.MidSqmUpliftPercent,
                        agreement.LargeSqmMin, agreement.LargeSqmReductionPercent,
                        agreement.Duration4UpliftPercent, agreement.Duration5UpliftPercent, agreement.Duration6UpliftPercent,
                    });

                foreach (var band in agreement.Bands)
                {
                    migrationBuilder.InsertData(
                        table: "ConcordatoRentBands",
                        columns: new[]
                        {
                            "Id", "TerritorialRentAgreementId", "ZoneName", "CadastralSheets",
                            "MinSqm", "MaxSqm",
                            "SubFascia1MinEurSqmYear", "SubFascia1MaxEurSqmYear",
                            "SubFascia2MinEurSqmYear", "SubFascia2MaxEurSqmYear",
                            "SubFascia3MinEurSqmYear", "SubFascia3MaxEurSqmYear",
                        },
                        values: new object[]
                        {
                            band.Id, band.TerritorialRentAgreementId, band.ZoneName, band.CadastralSheets,
                            band.MinSqm, band.MaxSqm,
                            band.SubFascia1MinEurSqmYear, band.SubFascia1MaxEurSqmYear,
                            band.SubFascia2MinEurSqmYear, band.SubFascia2MaxEurSqmYear,
                            band.SubFascia3MinEurSqmYear, band.SubFascia3MaxEurSqmYear,
                        });
                }

                foreach (var signatory in agreement.Signatories)
                {
                    migrationBuilder.InsertData(
                        table: "TerritorialAgreementSignatories",
                        columns: new[] { "Id", "TerritorialRentAgreementId", "Name", "Role", "Contact" },
                        values: new object[]
                        {
                            signatory.Id, signatory.TerritorialRentAgreementId,
                            signatory.Name, (int)signatory.Role, signatory.Contact,
                        });
                }
            }

            foreach (var ata in CanoneConcordatoMbSeed.BuildAtaCandidates())
            {
                migrationBuilder.InsertData(
                    table: "HighTensionAreaComuni",
                    columns: new[] { "Id", "Comune", "Region", "SourceReference", "VerifiedDirectly", "LastVerifiedAt" },
                    values: new object[]
                    {
                        ata.Id, ata.Comune, ata.Region, ata.SourceReference, ata.VerifiedDirectly, ata.LastVerifiedAt,
                    });
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConcordatoRentBands");

            migrationBuilder.DropTable(
                name: "HighTensionAreaComuni");

            migrationBuilder.DropTable(
                name: "TerritorialAgreementSignatories");

            migrationBuilder.DropTable(
                name: "TerritorialRentAgreements");
        }
    }
}
