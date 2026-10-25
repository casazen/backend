using System.Globalization;
using Casazen.Core.Documents;

namespace Casazen.Infrastructure.Services.CfSimulator;

/// <summary>The house of the contract a sample document speaks about: the address and the municipality of the property of the lease.</summary>
/// <param name="Address">Street and number, as the property has them.</param>
/// <param name="City">City as the property has it.</param>
/// <param name="PostalCode">Postal code, empty when the property has none.</param>
/// <param name="Comune">The municipality of the territorial agreement the contract names.</param>
public sealed record CfSimulatorHouse(string Address, string City, string PostalCode, string Comune)
{
    /// <summary>The house of a practice that arrived without a lease (or whose lease is gone): any house will do for a sample.</summary>
    public static CfSimulatorHouse Generic(string? comune) =>
        new("Via Roma 1", string.IsNullOrWhiteSpace(comune) ? "Milano" : comune.Trim(), "20100", string.IsNullOrWhiteSpace(comune) ? "Milano" : comune.Trim());
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
/// <para>The attestation and the receipt are short, with a number and a date: the system keeps them in the archive and does not read them.</para>
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

    public static PdfDocumentContent Receipt(CfSimulatorHouse house, string practiceId, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(house);

        return new PdfDocumentContent("RICEVUTA DI REGISTRAZIONE DEL CONTRATTO DI LOCAZIONE",
        [
            new PdfParagraph(SampleNotice, Bold: true),
            new PdfParagraph(
                $"Identificativo della registrazione: RLI-ESEMPIO-{Tail(practiceId)}\nData di registrazione: {Date(today)}\nNumero della pratica: {practiceId}"),
            new PdfParagraph(
                $"Il contratto di locazione ad uso abitativo a canone concordato dell'immobile sito in {house.City}, {house.Address}, "
                + "è stato registrato presso l'Agenzia delle Entrate (registrazione di esempio, nessun effetto)."),
            new PdfParagraph("Esito: registrazione eseguita."),
        ]);
    }

    /// <summary>The last 8 characters of the number of a practice, in capitals: the part that changes from one practice to another.</summary>
    private static string Tail(string practiceId) => practiceId.Length > 8 ? practiceId[^8..].ToUpperInvariant() : practiceId.ToUpperInvariant();

    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal amount) => amount.ToString("N2", ItalianNumbers);
}
