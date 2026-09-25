using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// Data of the LT-13 migration, with inline values: never reads <c>CanoneConcordatoMbSeed</c>, so the migration does
    /// the same thing whatever the reference data become later (A7-22, same convention as
    /// <c>LeaseContractTypeAndConcordatoRules.Data.cs</c>).
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>New admin permission <c>admin.ltr.manage</c> (RoleId 3 = admin), same pattern as <c>admin.seo.read</c> in
    /// <c>AddComplianceSeoEntities</c>.</item>
    /// <item>Every comune of the MB agreement: formal 18-month expiry from its effective date (F1 art. 14, class U),
    /// kept in force until a new agreement is signed, and the RS-8 note that no more recent agreement was found.</item>
    /// <item>The two pilot comuni's <c>VerificationSource</c> (RS-8's own check of the tables, LT-10).</item>
    /// <item>Comune offices receiving the IMU communication (Seveso, Cesano Maderno): destinatari, PEC and aliquota out
    /// of <c>ComuneImuNotificationService</c> and onto the database, an admin CRUD away from a fix (A7-22).</item>
    /// </list>
    /// </remarks>
    public partial class AddLtrReferenceDataAdmin
    {
        private const string MbAgreementName = "Accordo locale Quadro — Provincia di Monza e della Brianza";
        private const string MbRegion = "Lombardia";
        private const string MbSourceUrl = "https://municipium-images-production.s3-eu-west-1.amazonaws.com/s3/6875/allegati/accordo-canone-concordato-mb.pdf";

        // EffectiveDate (2024-05-01) + 18 months, the agreement's formal deposit term (F1 art. 14, class U).
        private const string ExpiresAtLiteral = "2025-11-01T00:00:00Z";

        private const string ExpiryNote =
            "Nessun accordo più recente reperito nelle ricerche (RS-8, agosto 2026) — verificare con le associazioni firmatarie o i Comuni prima di un uso vincolante.";

        private const string PilotVerificationSource =
            "RS-8: research-canone-concordato-mb.md, confronto con il testo ufficiale dell'accordo (PDF)";

        private static readonly string[] PilotComuni = ["Seveso", "Cesano Maderno"];

        /// <summary>Same id scheme as the MB seed (stable ids per comune and, here, per IMU channel).</summary>
        private static Guid MbId(string key) =>
            new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("casazen-mb-concordato:" + key)));

        private static readonly Guid SevesoImuChannelId = MbId("imu:Seveso");
        private static readonly Guid CesanoImuChannelId = MbId("imu:Cesano Maderno");

        private static void ApplyData(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "RolePermissions",
                columns: ["PermissionKey", "RoleId"],
                values: new object[] { "admin.ltr.manage", 3 });

            // No apostrophe in this text, so a plain literal is safe.
            migrationBuilder.Sql($"""
                UPDATE "TerritorialRentAgreements"
                SET "ExpiresAt" = '{ExpiresAtLiteral}',
                    "RemainsInForceUntilReplaced" = TRUE,
                    "ExpiryNote" = '{ExpiryNote}'
                WHERE "AgreementName" = '{MbAgreementName}' AND "Region" = '{MbRegion}';
                """);

            foreach (var comune in PilotComuni)
            {
                migrationBuilder.UpdateData(
                    table: "TerritorialRentAgreements",
                    keyColumn: "Id",
                    keyValue: MbId(comune),
                    column: "VerificationSource",
                    value: PilotVerificationSource);
            }

            migrationBuilder.InsertData(
                table: "ComuneImuChannels",
                columns:
                [
                    "Id", "Comune", "Region", "RecipientOffice", "Email", "Pec", "PostalAddress", "Instructions",
                    "RatePercent", "EffectiveRatePercent", "RateYear", "RateKind", "RateNotes", "RateSourceUrl",
                    "SourceUrl", "DataCompleteness",
                ],
                values:
                [
                    SevesoImuChannelId, "Seveso", MbRegion, "Ufficio Tributi",
                    "protocollo@comune.seveso.mb.it", "comune.seveso@pec.it", null,
                    "Canale ufficiale NON univoco — la ricerca registra anche tributi@comune.seveso.mb.it e il portale SPID/CIE/CNS Servizi Sociali (Contratti di locazione a canone concordato). Verificare con l'Ufficio Tributi prima dell'invio.",
                    null, null, null, null, null, null, MbSourceUrl, /* Partial */ 1,
                ]);

            migrationBuilder.InsertData(
                table: "ComuneImuChannels",
                columns:
                [
                    "Id", "Comune", "Region", "RecipientOffice", "Email", "Pec", "PostalAddress", "Instructions",
                    "RatePercent", "EffectiveRatePercent", "RateYear", "RateKind", "RateNotes", "RateSourceUrl",
                    "SourceUrl", "DataCompleteness",
                ],
                values:
                [
                    CesanoImuChannelId, "Cesano Maderno", MbRegion, "U.O. Risorse Tributarie",
                    "risorse.tributarie@comune.cesano-maderno.mb.it", "risorse.finanziarie@pec.comune.cesano-maderno.mb.it", null, null,
                    1.04m, 0.78m, 2025, /* Derived */ 1,
                    "Valore derivato (aliquota 1,04% x riduzione nazionale 75% ex L. 160/2019 art. 1 c. 760), NON un'aliquota ufficiale pubblicata. Delibera 2026 non reperita alla data della ricerca.",
                    null, MbSourceUrl, /* Partial */ 1,
                ]);
        }

        private static void RevertData(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(table: "ComuneImuChannels", keyColumn: "Id", keyValue: SevesoImuChannelId);
            migrationBuilder.DeleteData(table: "ComuneImuChannels", keyColumn: "Id", keyValue: CesanoImuChannelId);

            foreach (var comune in PilotComuni)
            {
                migrationBuilder.UpdateData(
                    table: "TerritorialRentAgreements",
                    keyColumn: "Id",
                    keyValue: MbId(comune),
                    column: "VerificationSource",
                    value: (string)null);
            }

            migrationBuilder.Sql($"""
                UPDATE "TerritorialRentAgreements"
                SET "ExpiresAt" = NULL, "RemainsInForceUntilReplaced" = FALSE, "ExpiryNote" = NULL
                WHERE "AgreementName" = '{MbAgreementName}' AND "Region" = '{MbRegion}';
                """);

            migrationBuilder.DeleteData(
                table: "RolePermissions",
                keyColumns: ["PermissionKey", "RoleId"],
                keyValues: new object[] { "admin.ltr.manage", 3 });
        }
    }
}
