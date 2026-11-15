using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Data.Seeds;

/// <summary>
/// The rows of the ISTAT region table (E21/F01/US01/T01, decision O6), applied by the migration <c>AddIstatRegionRules</c> through <c>HasData</c>: one for each
/// of the 19 regions that have a single portal and one for each of the two autonomous provinces of Trentino-Alto Adige, which have their own. They are
/// read from the matrix of <c>docs/runbooks/istat-flussi-regionali.md</c> (research of 26 October 2026) and from <c>docs/runbooks/ross1000.md</c>.
/// </summary>
/// <remarks>
/// <para><b>Nothing is guessed.</b> A value the runbook does not state stays «da verificare»: a deadline only where it names the day (Lombardia, Valle
/// d'Aosta and Umbria the 5th, Basilicata the 10th), a portal address only where it names the address, and the statistical fields the region requires
/// not at all (the runbook says none: <c>RequiredGuestFields</c> null, so no guest is asked one). <b>Every row is <see cref="IstatCoverage.Unknown"/>:</b>
/// the list of the regions the channel manager covers is not public (D15) and no seed may say covered. <c>VerifiedAt</c> is empty: the staff sets it when
/// it checks a row against its source.</para>
/// <para>The ids are fixed so the migration never changes, and so are the dates. Staff edits live in the table: a later seed change would be a new migration.</para>
/// </remarks>
public static class IstatRegionRuleSeed
{
    /// <summary>The date the rows were written (fixed: a migration must not change after it ran).</summary>
    public static readonly DateTime SeededAt = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);

    private const string Ross1000FamilyPage =
        "https://statistica.regione.emilia-romagna.it/metadati/rilevazioni/turismo/allegati-rilevazioni-turismo/manuali-e-tracciati-rilevazioni-turismo";

    /// <summary>New instances of the seeded rows on every call (entities are mutable).</summary>
    public static IReadOnlyList<IstatRegionRule> Build() =>
    [
        Row(
            1, "01", null, "Piemonte", "ROSS1000", null, IstatDeadlineKind.Unknown, null,
            Ross1000FamilyPage,
            "Matrice del runbook: ROSS1000, protocollo di famiglia, configurazione locale da acquisire. Nessuna pagina del Piemonte nel runbook: la fonte è la pagina della famiglia ROSS1000. Scadenza non indicata."),
        Row(
            2, "02", null, "Valle d'Aosta", "VIT Albergatori", null, IstatDeadlineKind.Monthly, 5,
            "https://www.regione.vda.it/gestione/vit_albergatori/Docs/VIT%20Albergatori%20Manuale%20utente.pdf",
            "Il manuale ufficiale conferma obbligatorietà, inserimento manuale e integrazione con PMS (contratto tecnico non pubblico). Chiusura mensile C59-M entro il giorno 5, inclusi i periodi chiusi."),
        Row(
            3, "03", null, "Lombardia", "ROSS1000 (Provincia di Sondrio: ABIT)", "https://www.flussituristici.servizirl.it/", IstatDeadlineKind.Monthly, 5,
            "https://www.cittametropolitana.mi.it/servizi/Comunicazione-dei-flussi-turistici-delle-strutture-ricettive/",
            "Ross1000 Lombardia: inserimento mensile entro il giorno 5 del mese successivo, anche senza movimento (L.R. 27/2015 art. 38 c. 8 e art. 40 c. 9; Città metropolitana di Milano, Provincia di Lecco). Provincia di Sondrio: ABIT, stessa scadenza del giorno 5. Campi statistici obbligatori: non chiari, da verificare."),
        Row(
            4, "04", "BZ", "Provincia autonoma di Bolzano", "ASTAT (IST-00139 mensile, TUR-031 giornaliera)", null, IstatDeadlineKind.Unknown, null,
            "https://astat.provinz.bz.it/it/turismo",
            "ASTAT gestisce la rilevazione mensile IST-00139 e, da novembre 2023, la giornaliera TUR-031. La pagina ufficiale pubblica guida e circolare correnti ma non un contratto PMS. Giorno di scadenza non indicato."),
        Row(
            5, "04", "TN", "Provincia autonoma di Trento", "STU (strutture ricettive); DTU Alloggi (alloggi privati)", null, IstatDeadlineKind.Unknown, null,
            "https://www.provincia.tn.it/Servizi/Comunicare-online-le-presenze-degli-ospiti-a-ISTAT-e-Pubblica-Sicurezza",
            "STU ammette inserimento manuale, file da gestionale abilitato e Web Service (test di congruenza con ISPAT). DTU Alloggi, per gli alloggi privati: inserimento online con SPID/CPS, nessuna API pubblica verificata. Scadenza non indicata."),
        Row(
            6, "05", null, "Veneto", "ROSS1000", null, IstatDeadlineKind.Unknown, null,
            "https://elezioni2020.regione.veneto.it/web/turismo/rilevazione-flussi-turistici-ross1000",
            "Matrice del runbook: ROSS1000, protocollo di famiglia. Scadenza non indicata."),
        Row(
            7, "06", null, "Friuli-Venezia Giulia", "WebTur", "https://servizistatistici.regione.fvg.it/webtur/jsp/wt/login.jsp", IstatDeadlineKind.Unknown, null,
            "https://servizistatistici.regione.fvg.it/webtur/jsp/wt/login.jsp",
            "Il portale ufficiale dichiara il caricamento da file anche per le unità abitative; il tracciato non è pubblico (assistenza Insiel). Scadenza non indicata."),
        Row(
            8, "07", null, "Liguria", "ROSS1000 e percorsi locali (RIMOVCLI)", null, IstatDeadlineKind.Unknown, null,
            Ross1000FamilyPage,
            "Non assumere una copertura uniforme dal solo nome della regione: si instrada per codice struttura ed ente competente. Nessuna pagina della Liguria nel runbook: la fonte è la pagina della famiglia ROSS1000. Scadenza non indicata."),
        Row(
            9, "08", null, "Emilia-Romagna", "ROSS1000", null, IstatDeadlineKind.Unknown, null,
            Ross1000FamilyPage,
            "Implementazione di riferimento: XML/WS v2.4, TXT v4, Web Service e demo ufficiale. Scadenza non indicata."),
        Row(
            10, "09", null, "Toscana", "Turismo5 (famiglia ROSS1000), Ricestat e sistemi territoriali: dipende dalla provincia", null, IstatDeadlineKind.Unknown, null,
            "https://www.regione.toscana.it/-/codice-identificativo-nazionale-cin-per-le-locazioni-turistiche-e-le-strutture-ricettive-turistiche",
            "Unicom per Arezzo; Turismo5 per Firenze e gli ambiti indicati, Pistoia e Prato; Ricestat per Grosseto, Livorno, Lucca, Massa-Carrara, Pisa e Siena. Il portale si sceglie per provincia, non per regione. Scadenza non indicata."),
        Row(
            11, "10", null, "Umbria", "Turismatica", null, IstatDeadlineKind.Monthly, 5,
            "https://www.regione.umbria.it/turismo-attivita-sportive/modalita-di-rilevazione-del-movimento-dei-clienti-negli-esercizi-ricettivi1",
            "File giornaliero TXT dal gestionale (o inserimento manuale), registrazione quotidiana di arrivo, partenza e camere occupate; chiusura mensile entro il giorno 5, anche a zero. La specifica «Istruzioni per interfacciamento» è da acquisire."),
        Row(
            12, "11", null, "Marche", "ROSS1000 (ISTRICE)", null, IstatDeadlineKind.Unknown, null,
            Ross1000FamilyPage,
            "Protocollo di famiglia, configurazione locale da acquisire. Nessuna pagina delle Marche nel runbook: la fonte è la pagina della famiglia ROSS1000. Scadenza non indicata."),
        Row(
            13, "12", null, "Lazio", "ROSS1000-Lazio (dal 21/05/2025)", null, IstatDeadlineKind.Unknown, null,
            "https://regione.lazio.it/cittadini/turismo/comunicazione-flussi-turistici-e-cir",
            "La pagina ufficiale dichiara l'import XML; il Web Service si abilita solo dopo la conferma della Regione. Scadenza non indicata."),
        Row(
            14, "13", null, "Abruzzo", "SITRA (ROSS1000)", null, IstatDeadlineKind.Unknown, null,
            "https://www.regione.abruzzo.it/novita/procedura-telematica-ottenere-il-cir",
            "Famiglia XML/TXT/WS di ROSS1000: endpoint e credenziali locali da confermare. Scadenza non indicata."),
        Row(
            15, "14", null, "Molise", null, null, IstatDeadlineKind.Unknown, null,
            "https://www.regione.molise.it/flex/cm/pages/ServeBLOB.php/L/IT/IDPagina/20744",
            "Il servizio regionale dei flussi non è identificato nelle fonti pubbliche consultate: non va classificato come ROSS1000 per analogia. Percorso assistito, con il contatto dell'ente competente."),
        Row(
            16, "15", null, "Campania", "Sinfonia Turismo SMART", null, IstatDeadlineKind.Unknown, null,
            "https://www.regione.campania.it/aree-tematiche/cultura-turismo/strutture-ricettive-locazioni-brevi/sinfonia-turismo-smart-sistema-monitoraggio-accoglienza-ricettivita-turistica",
            "Integrazione REST API ufficiale dal 2025 (CUSR e API key della struttura); documentazione tecnica e utenze di test su richiesta alle software house. Scadenza non indicata."),
        Row(
            17, "16", null, "Puglia", "DMS: SPOT Online (strutture ricettive), SPOT Easy (locazioni turistiche)", null, IstatDeadlineKind.Unknown, null,
            "https://dms.ect.regione.puglia.it/comefareper?pagina=faqprivateSpot",
            "SPOT Online: XML UTF-8 giornaliero (movimentogiornaliero-0.6.xsd), caricamento dopo l'accesso SPID. SPOT Easy, per le locazioni turistiche: inserimento sul portale; il caricamento del file di Pubblica Sicurezza non equivale a un flusso ISTAT. Scadenza non indicata."),
        Row(
            18, "17", null, "Basilicata", "SIST (basato su Turismo5/ROSS1000)", null, IstatDeadlineKind.Monthly, 10,
            "https://www.aptbasilicata.it/dati-statistici-2026/",
            "APT Basilicata dichiara che SIST è basato su Turismo5/ROSS1000; la rilevazione include le locazioni turistiche. Dati mensili da comunicare entro il giorno 10 (dati e obblighi 2026)."),
        Row(
            19, "18", null, "Calabria", "ROSS1000", null, IstatDeadlineKind.Unknown, null,
            "https://www.regione.calabria.it/cultura-turismo-ed-eventi/strutture-ricettive/informazioni-servizi-e-modulistica/documenti/",
            "Portale come scritto nel runbook: sirdat.regione.calabria.it (endpoint locale non pubblicato). Scadenza non indicata."),
        Row(
            20, "19", null, "Sicilia", "Osservatorio Turistico regionale", "https://osservatorioturistico.regione.sicilia.it/public/info", IstatDeadlineKind.Unknown, null,
            "https://osservatorioturistico.regione.sicilia.it/public/public/docs/Sistema_Informativo_Osservatorio_Turistico_ProtocolloPMS.pdf",
            "Protocollo PMS ufficiale rev. 1.0.7 del 03/04/2025: API HTTPS con bearer token e XML; credenziali PMS dedicate a una sola struttura. Scadenza non indicata."),
        Row(
            21, "20", null, "Sardegna", "SIRED (ROSS1000)", null, IstatDeadlineKind.Unknown, null,
            Ross1000FamilyPage,
            "Protocollo di famiglia, configurazione regionale da acquisire. Nessuna pagina della Sardegna nel runbook: la fonte è la pagina della famiglia ROSS1000. Scadenza non indicata."),
    ];

    private static IstatRegionRule Row(
        int number,
        string regionIstatCode,
        string? provinceCode,
        string regionName,
        string? system,
        string? portalUrl,
        IstatDeadlineKind deadlineKind,
        int? deadlineDay,
        string sourceUrl,
        string notes)
    {
        return new IstatRegionRule
        {
            Id = new Guid($"a1c39e50-7d24-4b68-9f03-5e8d2c6b{number:D4}"),
            RegionIstatCode = regionIstatCode,
            ProvinceCode = provinceCode,
            RegionName = regionName,
            System = system,
            PortalUrl = portalUrl,
            DeadlineKind = deadlineKind,
            DeadlineDay = deadlineDay,
            RequiredGuestFields = null,
            OctorateCoverage = IstatCoverage.Unknown,
            SourceUrl = sourceUrl,
            VerifiedAt = null,
            Notes = notes,
            UpdatedAt = SeededAt,
            UpdatedByUserId = null,
        };
    }
}
