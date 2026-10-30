using System.Globalization;
using Casazen.Core.Documents;

namespace Casazen.Infrastructure.Services.CfSimulator;

/// <summary>The house of the contract a sample document speaks about: the address and the municipality of the property of the lease.</summary>
/// <param name="Address">Street and number, as the property has them.</param>
/// <param name="City">City as the property has it.</param>
/// <param name="PostalCode">Postal code, empty when the property has none.</param>
/// <param name="Comune">The municipality of the territorial agreement the contract names.</param>
/// <param name="Province">The two letters of the province of the municipality, empty when it is not known (the receipt of the registration names it).</param>
public sealed record CfSimulatorHouse(string Address, string City, string PostalCode, string Comune, string Province = "")
{
    /// <summary>The house of a practice that arrived without a lease (or whose lease is gone): any house will do for a sample.</summary>
    public static CfSimulatorHouse Generic(string? comune, string? province = null) =>
        new(
            "Via Roma 1",
            string.IsNullOrWhiteSpace(comune) ? "Milano" : comune.Trim(),
            "20100",
            string.IsNullOrWhiteSpace(comune) ? "Milano" : comune.Trim(),
            string.IsNullOrWhiteSpace(province) ? string.Empty : province.Trim().ToUpperInvariant());
}

/// <summary>
/// The sample PDFs that the simulated practices deliver (CF-SIM), the three that ConcordatoFacile e-mails: the contract, the attestation
/// and the receipt of the registration. Each one says, in plain sight, that it is a sample made by the test environment.
/// </summary>
/// <remarks>
/// <para>The contract is written to be read by the local reading of the lease documents (<c>LeaseContractTextParser</c>, runbook
/// <c>lease-document-extraction.md</c>), in the layout of a form with the people in labelled lines, and it is the first test of the whole way
/// "real PDF to reading": it is signed today, starts on the first day of next month, is a canone concordato 3+2 with a monthly rent and a
/// deposit of two months, between two invented people whose fiscal codes pass the check character (Mario Rossi, Giulia Verdi), and names the
/// address and the municipality of the property of the lease, so the comparison with the property finds no conflict. It stays on one page,
/// because the footer of a page would cut a sentence that goes over to the next.</para>
/// <para>CF-DOCS-REAL: the contract is delivered as the real service delivers it, in one file with the attestation of conformity and the
/// calculation sheet (<see cref="ContractWithAttestation"/>, handed out as a scan), and the receipt of the registration is the one of the
/// Agenzia delle Entrate (<see cref="Receipt"/>, mod. RLI12, a digital PDF the system reads to fill in the declaration). The attestation
/// alone is kept for the old endpoint: short, with a number and a date, archived and not read.</para>
/// </remarks>
public static class CfSimulatorDocuments
{
    /// <summary>The sentence every sample document shows.</summary>
    public const string SampleNotice = "Documento di esempio generato dall'ambiente di prova.";

    /// <summary>The monthly rent of the sample contract (euro).</summary>
    public const decimal SampleMonthlyRent = 650m;

    /// <summary>The security deposit of the sample contract (euro): two months of rent.</summary>
    public const decimal SampleSecurityDeposit = 1300m;

    public const string LandlordFiscalCode = "RSSMRA80A01H501U";

    public const string TenantFiscalCode = "VRDGLI85B42F205E";

    // As the contracts of the templates write an amount (1.300,00), whatever the cultures of the host.
    private static readonly NumberFormatInfo ItalianNumbers = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        NumberDecimalDigits = 2,
    };

    /// <summary>The first day of the month after <paramref name="today"/>: when the sample contract starts.</summary>
    public static DateOnly StartOf(DateOnly today) => new DateOnly(today.Year, today.Month, 1).AddMonths(1);

    /// <summary>The last day of the first period: three years after the start, less a day (the 3 of a 3+2).</summary>
    public static DateOnly EndOf(DateOnly start) => start.AddYears(3).AddDays(-1);

    public static PdfDocumentContent Contract(CfSimulatorHouse house, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(house);

        var start = StartOf(today);
        var end = EndOf(start);
        var postal = string.IsNullOrWhiteSpace(house.PostalCode) ? string.Empty : $", {house.PostalCode.Trim()}";

        return new PdfDocumentContent("CONTRATTO DI LOCAZIONE AD USO ABITATIVO A CANONE CONCORDATO",
        [
            new PdfParagraph(SampleNotice, Bold: true),
            new PdfParagraph($"(art. 2, comma 3, legge 9 dicembre 1998 n. 431 - Accordo territoriale del Comune di {house.Comune})"),
            new PdfHeading("LOCATORE"),
            new PdfParagraph($"Cognome e Nome: ROSSI MARIO Codice fiscale: {LandlordFiscalCode}\nResidente in: ROMA (RM), Via dei Mille n. 5"),
            new PdfHeading("CONDUTTORE"),
            new PdfParagraph($"Cognome e Nome: VERDI GIULIA Codice fiscale: {TenantFiscalCode}\nResidente in: MILANO (MI), Via Argiro n. 7"),
            new PdfHeading("IMMOBILE"),
            new PdfParagraph($"L'unità immobiliare ad uso abitativo è sita in {house.City}, {house.Address}{postal}."),
            new PdfParagraph(
                $"Art. 1 - Durata. Il contratto ha durata di 3 (tre) anni a decorrere dal {Date(start)} fino al {Date(end)} ed è prorogato di diritto per due anni, salvo disdetta."),
            new PdfParagraph(
                $"Art. 2 - Canone. Il canone mensile di locazione, determinato ai sensi dell'Accordo territoriale, è di Euro {Money(SampleMonthlyRent)} (seicentocinquanta/00), da versare entro il giorno 5 di ogni mese."),
            new PdfParagraph(
                $"Art. 3 - Deposito cauzionale. Il Conduttore versa la somma di Euro {Money(SampleSecurityDeposit)} pari a due mensilità del canone."),
            new PdfParagraph("Art. 4 - Regime fiscale. Il Locatore non ha optato per la cedolare secca."),
            new PdfParagraph($"Letto, confermato e sottoscritto.\n{house.City}, li {Date(today)}"),
        ]);
    }

    /// <summary>The conventional surface of the house in the attestation and the sheet (square metres).</summary>
    public const decimal SampleSurface = 80m;

    /// <summary>The lowest monthly rent the sample agreement allows for the house (6 euro the square metre).</summary>
    public const decimal SampleRangeMinimum = 480m;

    /// <summary>The highest monthly rent the sample agreement allows for the house (9 euro the square metre).</summary>
    public const decimal SampleRangeMaximum = 720m;

    public const string OwnersOrganization = "ASSOCIAZIONE PROPRIETARI DI PROVA";

    public const string TenantsOrganization = "UNIONE INQUILINI DI PROVA";

    /// <summary>
    /// The one document the guided service delivers (CF-DOCS-REAL): the signed contract, the attestation of conformity and the calculation sheet
    /// of the rent, each starting its own page with the heading of the real ones ("Attestazione di Rispondenza", "Scheda Riepilogativa Canone").
    /// The simulator hands it out as a scan (<c>ScannedPdf</c>), so the reading of the whole round runs the OCR and tells the sections
    /// apart as it does for the real file. The attestation repeats the people and the dates of the contract and the rent, which is within the
    /// values it states; the sheet works the same figures out: a reading that mixed the sections up would find two rents, not one.
    /// </summary>
    public static PdfDocumentContent ContractWithAttestation(CfSimulatorHouse house, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(house);

        var contract = Contract(house, today);
        var start = StartOf(today);
        var end = EndOf(start);
        var comune = house.Comune;
        var province = string.IsNullOrWhiteSpace(house.Province) ? string.Empty : $" ({house.Province})";
        var agreement = $"Accordo locale per i Comuni dell'ambito di {comune}, sottoscritto in {comune} il 20 maggio 2025 - Comune di {comune}{province}";
        var annual = SampleMonthlyRent * 12m;
        var range = $"da EUR {Money(SampleRangeMinimum)} a EUR {Money(SampleRangeMaximum)} mensili (da EUR {Money(SampleRangeMinimum * 12m)} a EUR {Money(SampleRangeMaximum * 12m)} annui)";

        var blocks = new List<PdfBlock>(contract.Blocks)
        {
            new PdfHeading("Attestazione di Rispondenza", 1, NewPage: true),
            new PdfParagraph(SampleNotice, Bold: true),
            new PdfParagraph("(D.M. 16 gennaio 2017)"),
            new PdfParagraph(agreement),
            new PdfParagraph(
                $"L'Organizzazione della proprietà edilizia {OwnersOrganization}, con sede in Roma, in persona del proprio legale rappresentante pro tempore, "
                + $"e l'Organizzazione sindacale dei conduttori {TenantsOrganization}, aderente al medesimo Accordo, rilasciano congiuntamente la presente "
                + "attestazione ai sensi dell'art. 12 dell'Accordo e dell'art. 1, comma 8, del D.M. 16 gennaio 2017."),
            new PdfHeading("Parti e immobile"),
            new PdfParagraph(
                $"Parte locatrice: ROSSI MARIO, codice fiscale {LandlordFiscalCode}\nParte conduttrice: VERDI GIULIA, codice fiscale {TenantFiscalCode}\n"
                + $"Immobile: {house.City}, {house.Address}\nDurata del contratto: 3 anni, prorogabili di 2 (36 mesi di durata iniziale, 60 mesi complessivi) - dal {Date(start)} al {Date(end)}"),
            new PdfHeading("Accordo e contratto"),
            new PdfParagraph(
                $"L'unità immobiliare ricade nella Zona 1 del Comune di {comune}. Per la Zona 1, per la sub-fascia 3, i valori di riferimento vanno da EUR 6,00 a EUR 9,00 al mq per mese. "
                + $"Applicati alla superficie convenzionale di mq {Money(SampleSurface)}, i valori di riferimento della sub-fascia 3 corrispondono a un canone {range}."),
            new PdfParagraph($"Il canone convenuto fra le parti è di EUR {Money(SampleMonthlyRent)} mensili, pari a EUR {Money(annual)} annui, oltre oneri accessori."),
            new PdfHeading("Attestazione"),
            new PdfParagraph(
                "ATTESTANO che i contenuti economici del contratto di locazione sopra descritto rientrano nei valori previsti dall'Accordo territoriale vigente "
                + $"per il Comune di {comune}, depositato ai sensi dell'art. 7, comma 2, del D.M. 16 gennaio 2017."),
            new PdfParagraph(
                $"Sottoscrizioni\nLuogo e data {house.City}, {Date(today)}\n{OwnersOrganization} - Organizzazione della proprietà edilizia attestante\n"
                + $"{TenantsOrganization} - Organizzazione sindacale dei conduttori attestante - Timbro e firma"),

            new PdfHeading("Scheda Riepilogativa Canone", 1, NewPage: true),
            new PdfParagraph(SampleNotice, Bold: true),
            new PdfParagraph(agreement),
            new PdfParagraph("Contratto di locazione ad uso abitativo a canone concordato (agevolato 3+2), ai sensi dell'art. 2, comma 3, della legge 9 dicembre 1998 n. 431"),
            new PdfHeading("Accordo e parametri"),
            new PdfParagraph(
                $"Zona urbana omogenea: Zona 1 del Comune di {comune}\nSuperficie convenzionale mq {Money(SampleSurface)}, pari alla superficie catastale\n"
                + "Sub-fascia 3 - soglia di almeno 22 punti\nValori di riferimento della sub-fascia 3 da EUR 6,00 a EUR 9,00 al mq per mese"),
            new PdfHeading("Determinazione del canone"),
            new PdfParagraph(
                $"Canone minimo di sub-fascia EUR {Money(SampleRangeMinimum)} mensili - EUR {Money(SampleRangeMinimum * 12m)} annui\n"
                + $"Canone massimo teorico EUR {Money(SampleRangeMaximum)} mensili - EUR {Money(SampleRangeMaximum * 12m)} annui\n"
                + $"Canone convenuto EUR {Money(SampleMonthlyRent)} mensili - EUR {Money(annual)} annui"),
            new PdfHeading("Esito"),
            new PdfParagraph(
                $"Il canone convenuto di EUR {Money(SampleMonthlyRent)} mensili (EUR {Money(annual)} annui) è compreso fra il minimo di EUR {Money(SampleRangeMinimum)} "
                + $"e il massimo di EUR {Money(SampleRangeMaximum)} mensili della sub-fascia 3 per la Zona 1 del Comune di {comune}. "
                + "Il canone è pertanto conforme all'Accordo locale."),
        };

        return new PdfDocumentContent(contract.Title, blocks);
    }

    public static PdfDocumentContent Attestation(CfSimulatorHouse house, string practiceId, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(house);

        return new PdfDocumentContent("ATTESTAZIONE DI RISPONDENZA DEL CONTRATTO ALL'ACCORDO TERRITORIALE",
        [
            new PdfParagraph(SampleNotice, Bold: true),
            new PdfParagraph($"Numero dell'attestazione: ATT-{today.Year}-{Tail(practiceId)}\nData di rilascio: {Date(today)}\nNumero della pratica: {practiceId}"),
            new PdfParagraph(
                $"Si attesta che il contratto di locazione ad uso abitativo a canone concordato relativo all'immobile sito in {house.City}, {house.Address}, "
                + $"è rispondente a quanto previsto dall'Accordo territoriale del Comune di {house.Comune}, ai sensi dell'art. 2, comma 3, della legge 9 dicembre 1998 n. 431."),
            new PdfParagraph("L'attestazione è stata rilasciata dall'associazione indicata nella pratica, dopo la verifica dei dati del contratto."),
        ]);
    }

    /// <summary>
    /// The receipt of the registration as the Agenzia delle Entrate returns it (CF-DOCS-REAL, mod. RLI12): a digital PDF with the notice of the
    /// file received, the receipt with the office, the identification code, the number and series, the figures of the contract and the town, and
    /// the list of the documents acquired. The wording and the order of the lines are those of the real receipt; every number is invented and
    /// the first line says it is a sample. The contract of the sample does not choose the cedolare secca, so the receipt says nothing of it.
    /// </summary>
    public static PdfDocumentContent Receipt(CfSimulatorHouse house, string practiceId, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(house);

        var start = StartOf(today);
        var end = EndOf(start);
        var number = SixDigits(practiceId);
        var code = $"SIM{today.ToString("yy", CultureInfo.InvariantCulture)}T{number}000AB";
        var protocol = $"{today.ToString("yyMMdd", CultureInfo.InvariantCulture)}{number}00001";
        var province = string.IsNullOrWhiteSpace(house.Province) ? "MI" : house.Province;
        var annual = SampleMonthlyRent * 12m;

        return new PdfDocumentContent("SERVIZIO TELEMATICO ENTRATEL DI PRESENTAZIONE DELLE DICHIARAZIONI",
        [
            new PdfParagraph(SampleNotice, Bold: true),
            new PdfParagraph("COMUNICAZIONE DI AVVENUTO RICEVIMENTO (art. 3, comma 10, D.P.R. 322/1998)", Bold: true),
            new PdfParagraph("COMUNICAZIONE DI AVVENUTO RICEVIMENTO RICHIESTA DI REGISTRAZIONE CONTRATTI DI LOCAZIONE E AFFITTO DI IMMOBILI (mod.RLI12)"),
            new PdfParagraph(
                $"In data {Date(today)} alle ore 10:15:20 il sistema informativo dell'Agenzia delle Entrate ha acquisito con protocollo {protocol} il file\n"
                + "contenente 1 contratto di locazione, di cui\nregistrati: 1\nscartati: 0"),
            new PdfParagraph("Il file e' stato trasmesso da:\ncodice fiscale : 00000000000\ndenominazione : ESEMPIO AMBIENTE DI PROVA"),
            new PdfParagraph(
                "Si rilascia la presente ricevuta ai sensi dell'art.22, comma 4, del Decreto Dirigenziale 31 luglio 1998 e successive modificazioni.\n"
                + $"Li, {Date(today)}"),
            new PdfHeading("RICEVUTA DI AVVENUTA REGISTRAZIONE CONTRATTI DI LOCAZIONE (mod.RLI12)", 2, NewPage: true),
            new PdfParagraph($"Ufficio territoriale di {house.City.ToUpperInvariant()} 1\nCodice Identificativo del contratto {code}"),
            new PdfParagraph(
                $"In data {Date(today)} il sistema informativo dell'Agenzia delle Entrate ha acquisito con protocollo {protocol} - 000001 la richiesta di registrazione "
                + "telematica del contratto di locazione presentata da ESEMPIO e trasmessa da ESEMPIO AMBIENTE DI PROVA"),
            new PdfParagraph($"Il contratto e' stato registrato il {Date(today)} al n. {number} serie 3T e codice identificativo {code}"),
            new PdfParagraph(
                "DATI DEL CONTRATTO (importi in euro)\nIdentificativo assegnato dal richiedente : " + Tail(practiceId) + "\n"
                + $"Durata dal {Date(start)} al {Date(end)} Data di stipula {Date(today)}\n"
                + $"Importo del canone {Money(annual)} n.fogli 3 n.copie 1\n"
                + "Tipologia: Locazione agevolata immobile uso abitativo\nE' presente un file allegato."),
            new PdfParagraph(
                "SOGGETTI DESTINATARI DEGLI EFFETTI GIURIDICI DEL CONTRATTO\nN. CODICE FISCALE RUOLO N. CODICE FISCALE RUOLO\n"
                + $"001 {LandlordFiscalCode} A 001 {TenantFiscalCode} B\n(A) locatore / (B) conduttore"),
            new PdfParagraph(
                "DATI DEGLI IMMOBILI (importi in euro)\n-N.progr. 001 Categoria cat. A2 Rendita cat. 500,00\nTipologia immobile IMMOBILE Tipo catasto URBANO\n"
                + $"Ubicato nel comune di {house.Comune.ToUpperInvariant()} Prov. {province}\n{house.Address.ToUpperInvariant()}"),
            new PdfParagraph($"Li, {Date(today)}"),
            new PdfParagraph(
                "ELENCO DEI DOCUMENTI ACQUISITI E/O SCARTATI\nPROTOCOLLO DI RICEZIONE : " + protocol + "\n"
                + "TIPO DI DOCUMENTO : Registrazione dei contratti di locazione e di affitto\nDOCUMENTI ACQUISITI : 1\nDOCUMENTI SCARTATI : 0\n"
                + $"Esito Protocollo documenti Codice fiscale Denominazione\nacquisito {protocol} ----------- ESEMPIO"),
        ]);
    }

    /// <summary>Six digits taken from the number of a practice: what changes from one sample receipt to another (the code and the number).</summary>
    private static string SixDigits(string practiceId) =>
        uint.TryParse(Tail(practiceId), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            ? (value % 1_000_000u).ToString("D6", CultureInfo.InvariantCulture)
            : "000000";

    /// <summary>The last 8 characters of the number of a practice, in capitals: the part that changes from one practice to another.</summary>
    private static string Tail(string practiceId) => practiceId.Length > 8 ? practiceId[^8..].ToUpperInvariant() : practiceId.ToUpperInvariant();

    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal amount) => amount.ToString("N2", ItalianNumbers);
}
