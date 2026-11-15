using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIstatRegionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IstatRegionRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegionIstatCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    ProvinceCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    RegionName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    System = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PortalUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeadlineKind = table.Column<int>(type: "integer", nullable: false),
                    DeadlineDay = table.Column<int>(type: "integer", nullable: true),
                    RequiredGuestFields = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    OctorateCoverage = table.Column<int>(type: "integer", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IstatRegionRules", x => x.Id);
                    table.CheckConstraint("CK_IstatRegionRules_Deadline", "(\"DeadlineKind\" = 2 AND \"DeadlineDay\" BETWEEN 1 AND 31) OR (\"DeadlineKind\" <> 2 AND \"DeadlineDay\" IS NULL)");
                    table.CheckConstraint("CK_IstatRegionRules_Enums", "\"DeadlineKind\" IN (0, 1, 2) AND \"OctorateCoverage\" IN (0, 1, 2)");
                    table.CheckConstraint("CK_IstatRegionRules_PortalUrlHttps", "\"PortalUrl\" IS NULL OR \"PortalUrl\" LIKE 'https://%'");
                    table.CheckConstraint("CK_IstatRegionRules_ProvinceCode", "\"ProvinceCode\" IS NULL OR \"ProvinceCode\" ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("CK_IstatRegionRules_RegionIstatCode", "\"RegionIstatCode\" ~ '^[0-9]{2}$'");
                    table.CheckConstraint("CK_IstatRegionRules_RequiredGuestFields", "\"RequiredGuestFields\" IS NULL OR \"RequiredGuestFields\" ~ '^((TourismType|TravelWay|EducationLevel)(,(TourismType|TravelWay|EducationLevel))*)?$'");
                    table.CheckConstraint("CK_IstatRegionRules_SourceUrlHttps", "\"SourceUrl\" LIKE 'https://%'");
                });

            migrationBuilder.InsertData(
                table: "IstatRegionRules",
                columns: new[] { "Id", "DeadlineDay", "DeadlineKind", "Notes", "OctorateCoverage", "PortalUrl", "ProvinceCode", "RegionIstatCode", "RegionName", "RequiredGuestFields", "SourceUrl", "System", "UpdatedAt", "UpdatedByUserId", "VerifiedAt" },
                values: new object[,]
                {
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0001"), null, 0, "Matrice del runbook: ROSS1000, protocollo di famiglia, configurazione locale da acquisire. Nessuna pagina del Piemonte nel runbook: la fonte è la pagina della famiglia ROSS1000. Scadenza non indicata.", 0, null, null, "01", "Piemonte", null, "https://statistica.regione.emilia-romagna.it/metadati/rilevazioni/turismo/allegati-rilevazioni-turismo/manuali-e-tracciati-rilevazioni-turismo", "ROSS1000", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0002"), 5, 2, "Il manuale ufficiale conferma obbligatorietà, inserimento manuale e integrazione con PMS (contratto tecnico non pubblico). Chiusura mensile C59-M entro il giorno 5, inclusi i periodi chiusi.", 0, null, null, "02", "Valle d'Aosta", null, "https://www.regione.vda.it/gestione/vit_albergatori/Docs/VIT%20Albergatori%20Manuale%20utente.pdf", "VIT Albergatori", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0003"), 5, 2, "Ross1000 Lombardia: inserimento mensile entro il giorno 5 del mese successivo, anche senza movimento (L.R. 27/2015 art. 38 c. 8 e art. 40 c. 9; Città metropolitana di Milano, Provincia di Lecco). Provincia di Sondrio: ABIT, stessa scadenza del giorno 5. Campi statistici obbligatori: non chiari, da verificare.", 0, "https://www.flussituristici.servizirl.it/", null, "03", "Lombardia", null, "https://www.cittametropolitana.mi.it/servizi/Comunicazione-dei-flussi-turistici-delle-strutture-ricettive/", "ROSS1000 (Provincia di Sondrio: ABIT)", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0004"), null, 0, "ASTAT gestisce la rilevazione mensile IST-00139 e, da novembre 2023, la giornaliera TUR-031. La pagina ufficiale pubblica guida e circolare correnti ma non un contratto PMS. Giorno di scadenza non indicato.", 0, null, "BZ", "04", "Provincia autonoma di Bolzano", null, "https://astat.provinz.bz.it/it/turismo", "ASTAT (IST-00139 mensile, TUR-031 giornaliera)", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0005"), null, 0, "STU ammette inserimento manuale, file da gestionale abilitato e Web Service (test di congruenza con ISPAT). DTU Alloggi, per gli alloggi privati: inserimento online con SPID/CPS, nessuna API pubblica verificata. Scadenza non indicata.", 0, null, "TN", "04", "Provincia autonoma di Trento", null, "https://www.provincia.tn.it/Servizi/Comunicare-online-le-presenze-degli-ospiti-a-ISTAT-e-Pubblica-Sicurezza", "STU (strutture ricettive); DTU Alloggi (alloggi privati)", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0006"), null, 0, "Matrice del runbook: ROSS1000, protocollo di famiglia. Scadenza non indicata.", 0, null, null, "05", "Veneto", null, "https://elezioni2020.regione.veneto.it/web/turismo/rilevazione-flussi-turistici-ross1000", "ROSS1000", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0007"), null, 0, "Il portale ufficiale dichiara il caricamento da file anche per le unità abitative; il tracciato non è pubblico (assistenza Insiel). Scadenza non indicata.", 0, "https://servizistatistici.regione.fvg.it/webtur/jsp/wt/login.jsp", null, "06", "Friuli-Venezia Giulia", null, "https://servizistatistici.regione.fvg.it/webtur/jsp/wt/login.jsp", "WebTur", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0008"), null, 0, "Non assumere una copertura uniforme dal solo nome della regione: si instrada per codice struttura ed ente competente. Nessuna pagina della Liguria nel runbook: la fonte è la pagina della famiglia ROSS1000. Scadenza non indicata.", 0, null, null, "07", "Liguria", null, "https://statistica.regione.emilia-romagna.it/metadati/rilevazioni/turismo/allegati-rilevazioni-turismo/manuali-e-tracciati-rilevazioni-turismo", "ROSS1000 e percorsi locali (RIMOVCLI)", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0009"), null, 0, "Implementazione di riferimento: XML/WS v2.4, TXT v4, Web Service e demo ufficiale. Scadenza non indicata.", 0, null, null, "08", "Emilia-Romagna", null, "https://statistica.regione.emilia-romagna.it/metadati/rilevazioni/turismo/allegati-rilevazioni-turismo/manuali-e-tracciati-rilevazioni-turismo", "ROSS1000", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0010"), null, 0, "Unicom per Arezzo; Turismo5 per Firenze e gli ambiti indicati, Pistoia e Prato; Ricestat per Grosseto, Livorno, Lucca, Massa-Carrara, Pisa e Siena. Il portale si sceglie per provincia, non per regione. Scadenza non indicata.", 0, null, null, "09", "Toscana", null, "https://www.regione.toscana.it/-/codice-identificativo-nazionale-cin-per-le-locazioni-turistiche-e-le-strutture-ricettive-turistiche", "Turismo5 (famiglia ROSS1000), Ricestat e sistemi territoriali: dipende dalla provincia", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0011"), 5, 2, "File giornaliero TXT dal gestionale (o inserimento manuale), registrazione quotidiana di arrivo, partenza e camere occupate; chiusura mensile entro il giorno 5, anche a zero. La specifica «Istruzioni per interfacciamento» è da acquisire.", 0, null, null, "10", "Umbria", null, "https://www.regione.umbria.it/turismo-attivita-sportive/modalita-di-rilevazione-del-movimento-dei-clienti-negli-esercizi-ricettivi1", "Turismatica", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0012"), null, 0, "Protocollo di famiglia, configurazione locale da acquisire. Nessuna pagina delle Marche nel runbook: la fonte è la pagina della famiglia ROSS1000. Scadenza non indicata.", 0, null, null, "11", "Marche", null, "https://statistica.regione.emilia-romagna.it/metadati/rilevazioni/turismo/allegati-rilevazioni-turismo/manuali-e-tracciati-rilevazioni-turismo", "ROSS1000 (ISTRICE)", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0013"), null, 0, "La pagina ufficiale dichiara l'import XML; il Web Service si abilita solo dopo la conferma della Regione. Scadenza non indicata.", 0, null, null, "12", "Lazio", null, "https://regione.lazio.it/cittadini/turismo/comunicazione-flussi-turistici-e-cir", "ROSS1000-Lazio (dal 21/05/2025)", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0014"), null, 0, "Famiglia XML/TXT/WS di ROSS1000: endpoint e credenziali locali da confermare. Scadenza non indicata.", 0, null, null, "13", "Abruzzo", null, "https://www.regione.abruzzo.it/novita/procedura-telematica-ottenere-il-cir", "SITRA (ROSS1000)", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0015"), null, 0, "Il servizio regionale dei flussi non è identificato nelle fonti pubbliche consultate: non va classificato come ROSS1000 per analogia. Percorso assistito, con il contatto dell'ente competente.", 0, null, null, "14", "Molise", null, "https://www.regione.molise.it/flex/cm/pages/ServeBLOB.php/L/IT/IDPagina/20744", null, new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0016"), null, 0, "Integrazione REST API ufficiale dal 2025 (CUSR e API key della struttura); documentazione tecnica e utenze di test su richiesta alle software house. Scadenza non indicata.", 0, null, null, "15", "Campania", null, "https://www.regione.campania.it/aree-tematiche/cultura-turismo/strutture-ricettive-locazioni-brevi/sinfonia-turismo-smart-sistema-monitoraggio-accoglienza-ricettivita-turistica", "Sinfonia Turismo SMART", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0017"), null, 0, "SPOT Online: XML UTF-8 giornaliero (movimentogiornaliero-0.6.xsd), caricamento dopo l'accesso SPID. SPOT Easy, per le locazioni turistiche: inserimento sul portale; il caricamento del file di Pubblica Sicurezza non equivale a un flusso ISTAT. Scadenza non indicata.", 0, null, null, "16", "Puglia", null, "https://dms.ect.regione.puglia.it/comefareper?pagina=faqprivateSpot", "DMS: SPOT Online (strutture ricettive), SPOT Easy (locazioni turistiche)", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0018"), 10, 2, "APT Basilicata dichiara che SIST è basato su Turismo5/ROSS1000; la rilevazione include le locazioni turistiche. Dati mensili da comunicare entro il giorno 10 (dati e obblighi 2026).", 0, null, null, "17", "Basilicata", null, "https://www.aptbasilicata.it/dati-statistici-2026/", "SIST (basato su Turismo5/ROSS1000)", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0019"), null, 0, "Portale come scritto nel runbook: sirdat.regione.calabria.it (endpoint locale non pubblicato). Scadenza non indicata.", 0, null, null, "18", "Calabria", null, "https://www.regione.calabria.it/cultura-turismo-ed-eventi/strutture-ricettive/informazioni-servizi-e-modulistica/documenti/", "ROSS1000", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0020"), null, 0, "Protocollo PMS ufficiale rev. 1.0.7 del 03/04/2025: API HTTPS con bearer token e XML; credenziali PMS dedicate a una sola struttura. Scadenza non indicata.", 0, "https://osservatorioturistico.regione.sicilia.it/public/info", null, "19", "Sicilia", null, "https://osservatorioturistico.regione.sicilia.it/public/public/docs/Sistema_Informativo_Osservatorio_Turistico_ProtocolloPMS.pdf", "Osservatorio Turistico regionale", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null },
                    { new Guid("a1c39e50-7d24-4b68-9f03-5e8d2c6b0021"), null, 0, "Protocollo di famiglia, configurazione regionale da acquisire. Nessuna pagina della Sardegna nel runbook: la fonte è la pagina della famiglia ROSS1000. Scadenza non indicata.", 0, null, null, "20", "Sardegna", null, "https://statistica.regione.emilia-romagna.it/metadati/rilevazioni/turismo/allegati-rilevazioni-turismo/manuali-e-tracciati-rilevazioni-turismo", "SIRED (ROSS1000)", new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), null, null }
                });

            migrationBuilder.CreateIndex(
                name: "UIX_IstatRegionRules_Region",
                table: "IstatRegionRules",
                column: "RegionIstatCode",
                unique: true,
                filter: "\"ProvinceCode\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_IstatRegionRules_Region_Province",
                table: "IstatRegionRules",
                columns: new[] { "RegionIstatCode", "ProvinceCode" },
                unique: true,
                filter: "\"ProvinceCode\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IstatRegionRules");
        }
    }
}
