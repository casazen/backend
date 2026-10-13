using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// Data of the LT-10 migration, with inline values: never reads the seed classes, so the migration does the same
    /// thing whatever the reference data become later (A7-22). The current data are
    /// <c>CanoneConcordatoMbSeed</c>; a test checks that a migrated database matches it.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>Existing leases: contract type and tax regime from the legacy fiscal regime (A7-13). A canone concordato lease
    /// did not say its tax regime: it stays null, never guessed.</item>
    /// <item>MB agreement (RS-8, class U): the rules the calculation needs (sub-fascia thresholds, qualifying D elements,
    /// stoves, air conditioning, appurtenances), Seveso and Cesano bands as contiguous half-open intervals (A7-10), the 11
    /// signatory organizations, the verification date of the tables, and Misinto (Missing), the 55th comune.</item>
    /// </list>
    /// </remarks>
    public partial class LeaseContractTypeAndConcordatoRules
    {
        private const string MbAgreementName = "Accordo locale Quadro — Provincia di Monza e della Brianza";
        private const string MbRegion = "Lombardia";
        private const string MbSourceUrl = "https://municipium-images-production.s3-eu-west-1.amazonaws.com/s3/6875/allegati/accordo-canone-concordato-mb.pdf";

        private static readonly (string Name, int Role, string Contact)[] MbSignatories =
        [
            ("CONIA", 1, "Milano, viale Monza 137 · 02 2814151"),
            ("SICET", 1, "Monza, via Dante 17/A · 039 2399259"),
            ("SUNIA", 1, "Monza, via Premuda 17 · 039 2731201 · 345 6035702"),
            ("UNIAT", 1, "Monza, via Ardigò 15/A"),
            ("A.P.E. Monza (aderente Confedilizia)", 0, "Monza, via Mosè Bianchi 18/A · 342 5745184"),
            ("A.S.P.P.I. Comprensorio Brianza", 0, "Seveso, via L. Maderna 4 · 393 6435891"),
            ("CONFABITARE", 0, "Monza, via F. Magellano 21 · 380 6929090"),
            ("CONFAPPI", 0, "Monza, via Ponchielli 47 · 335 5368700"),
            ("FEDERPROPRIETÀ", 0, "Milano, viale Certosa 1 · 02 45478950"),
            ("U.P.P.I.", 0, "Monza, via G.F. Parravicini 30 · 02 2047734"),
            ("UNIONCASA", 0, "Limbiate, corso Milano 14 · 328 9050358 · 02 97135036"),
        ];

        /// <summary>The three signatories of the 2024 seed, restored by <see cref="Down"/>.</summary>
        private static readonly (string Name, int Role, string Contact)[] Seed2024Signatories =
        [
            ("A.S.P.P.I. Comprensorio Brianza", 0, "393 6435891"),
            ("Confabitare Monza Brianza", 0, "monzabrianza@confabitare.it"),
            ("SUNIA-CGIL Monza e Brianza", 1, "suniabrianza@cgil.lombardia.it"),
        ];

        private static readonly string[] PilotComuni = ["Seveso", "Cesano Maderno"];

        /// <summary>Same id scheme as the MB seed (stable ids per comune, band, signatory).</summary>
        private static Guid MbId(string key) =>
            new(MD5.HashData(Encoding.UTF8.GetBytes("casazen-mb-concordato:" + key)));

        private static void ApplyData(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "LeaseContracts"
                SET "ContractType" = CASE "FiscalRegime" WHEN 2 THEN 1 ELSE 0 END,
                    "TaxRegime" = CASE "FiscalRegime" WHEN 0 THEN 0 WHEN 1 THEN 1 ELSE NULL END;
                """);

            migrationBuilder.Sql($"""
                UPDATE "TerritorialRentAgreements"
                SET "SubFascia2MinTypeBCount" = 3,
                    "SubFascia3MinTypeCCount" = 3,
                    "SubFascia3MinQualifyingTypeDCount" = 2,
                    "SubFascia3QualifyingTypeDElements" = 'D1,D2,D4,D6,D7,D9',
                    "SubFascia3MaxMinTypeDCount" = 4,
                    "StoveHeatingMinTypeBCount" = 4,
                    "CoefficientCombination" = 0,
                    "AirConditioningUpliftPercent" = 5,
                    "GarageAppurtenancePercent" = 50,
                    "BalconyAppurtenancePercent" = 30,
                    "OtherAppurtenancePercent" = 25,
                    "GreenAreaAppurtenancePercent" = 10
                WHERE "AgreementName" = '{MbAgreementName}' AND "Region" = '{MbRegion}';
                """);

            foreach (var comune in PilotComuni)
            {
                var agreementId = MbId(comune);
                migrationBuilder.Sql($"""
                    UPDATE "TerritorialRentAgreements" SET "LastVerifiedAt" = '2026-09-23T00:00:00Z' WHERE "Id" = '{agreementId}';
                    UPDATE "ConcordatoRentBands" SET "MinSqm" = "MinSqm" - 1
                    WHERE "TerritorialRentAgreementId" = '{agreementId}' AND "MinSqm" IN (51, 75, 100);
                    DELETE FROM "TerritorialAgreementSignatories" WHERE "TerritorialRentAgreementId" = '{agreementId}';
                    """);
                InsertSignatories(migrationBuilder, agreementId, MbSignatories);
            }

            migrationBuilder.InsertData(
                table: "TerritorialRentAgreements",
                columns:
                [
                    "Id", "Comune", "Region", "AgreementName", "SignedDate", "EffectiveDate", "SourceUrl",
                    "DataCompleteness", "LastVerifiedAt", "RequiredTypeACount",
                    "SubFascia2MinTypeBCount", "SubFascia3MinTypeCCount", "SubFascia3MinQualifyingTypeDCount",
                    "SubFascia3QualifyingTypeDElements", "SubFascia3MaxMinTypeDCount", "StoveHeatingMinTypeBCount",
                    "CoefficientCombination", "FurnishedUpliftPercent", "AirConditioningUpliftPercent",
                    "SmallSqmMax", "SmallSqmUpliftPercent", "MidSqmMin", "MidSqmMax", "MidSqmUpliftPercent",
                    "LargeSqmMin", "LargeSqmReductionPercent",
                    "GarageAppurtenancePercent", "BalconyAppurtenancePercent", "OtherAppurtenancePercent",
                    "GreenAreaAppurtenancePercent",
                    "Duration4UpliftPercent", "Duration5UpliftPercent", "Duration6UpliftPercent",
                ],
                values:
                [
                    MbId("Misinto"), "Misinto", MbRegion, MbAgreementName,
                    new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    MbSourceUrl, 2, null, 2,
                    3, 3, 2, "D1,D2,D4,D6,D7,D9", 4, 4,
                    0, 15m, 5m,
                    40, 20m, 50, 60, 10m,
                    120, 20m,
                    50m, 30m, 25m, 10m,
                    3m, 5m, 6m,
                ]);
        }

        private static void RevertData(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(table: "TerritorialRentAgreements", keyColumn: "Id", keyValue: MbId("Misinto"));

            foreach (var comune in PilotComuni)
            {
                var agreementId = MbId(comune);
                migrationBuilder.Sql($"""
                    UPDATE "TerritorialRentAgreements" SET "LastVerifiedAt" = NULL WHERE "Id" = '{agreementId}';
                    UPDATE "ConcordatoRentBands" SET "MinSqm" = "MinSqm" + 1
                    WHERE "TerritorialRentAgreementId" = '{agreementId}' AND "MinSqm" IN (50, 74, 99);
                    DELETE FROM "TerritorialAgreementSignatories" WHERE "TerritorialRentAgreementId" = '{agreementId}';
                    """);
                InsertSignatories(migrationBuilder, agreementId, Seed2024Signatories);
            }
        }

        private static void InsertSignatories(
            MigrationBuilder migrationBuilder, Guid agreementId, (string Name, int Role, string Contact)[] signatories)
        {
            foreach (var (name, role, contact) in signatories)
            {
                migrationBuilder.InsertData(
                    table: "TerritorialAgreementSignatories",
                    columns: ["Id", "TerritorialRentAgreementId", "Name", "Role", "Contact"],
                    values: [MbId($"{agreementId:N}:{name}"), agreementId, name, role, contact]);
            }
        }
    }
}
