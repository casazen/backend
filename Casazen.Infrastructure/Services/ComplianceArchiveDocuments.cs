using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The documents of the proof archive, in Italian (E03/F02/US03/T01, NF-47): the <c>index.csv</c> of the export of the proofs and the PDF and
/// the CSV of the coverage report. The PDF is laid out by <see cref="IPdfDocumentRenderer"/>; the CSV by <see cref="CsvWriter"/> (UTF-8 with a BOM,
/// <c>;</c>, a cell a spreadsheet could run as a formula is guarded). Dates are written as <c>yyyy-MM-dd</c>, never as a date with a time format.
/// </summary>
/// <remarks>
/// <para><b>What a row of the report says.</b> Only what CasaZen knows. A row held only by Octorate says <see cref="CheckInOctorate"/> and has
/// <see cref="OctorateSource"/> as its source: it is not a proof, and its outcome (sent, accepted, rejected) is never inferred or written. A
/// row with a proof says where the proof comes from. A missing one says so. The report never claims to be issued, certified or verified by an
/// authority: it says it is made by CasaZen from its archive (<see cref="NotIssuedByAnAuthority"/>).</para>
/// </remarks>
public static class ComplianceArchiveDocuments
{
    /// <summary>What a row says when the outcome is held only by Octorate and CasaZen has no proof.</summary>
    public const string CheckInOctorate = "Da verificare nel Cassetto Compliance";

    /// <summary>The source of a row held only by Octorate.</summary>
    public const string OctorateSource = "Octorate";

    /// <summary>What the report says about itself: it is CasaZen's own summary, not an act of an authority.</summary>
    public const string NotIssuedByAnAuthority =
        "Documento generato da CasaZen dalle prove archiviate e dai dati dell'organizzazione. Non è emesso, certificato né verificato da "
        + "alcuna autorità e non sostituisce le ricevute ufficiali.";

    /// <summary>The name of the coverage report inside the ZIP of the proofs (with the traffic light on).</summary>
    public const string CoverageFileName = "rapporto-copertura.csv";

    private const string Dash = "-";

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    // ---------------------------------------------------------------- index.csv of the export

    /// <summary>The header of <c>index.csv</c>.</summary>
    public static readonly IReadOnlyList<string> IndexHeader =
        ["Tipo", "Immobile", "Giorno o periodo", "Origine", "Ricevuta il (UTC)", "Conservata fino al (UTC)", "File"];

    /// <summary>
    /// One row of <c>index.csv</c>: the kind, the property, the day or the month, where the proof comes from, when CasaZen received it, until
    /// when it keeps it, and the name of the file in the ZIP (<paramref name="fileName"/> is <c>null</c> when the file could not be read, then
    /// the cell says so).
    /// </summary>
    public static IReadOnlyList<object?> IndexRow(ComplianceArchiveEntry entry, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return
        [
            KindLabel(entry.Kind),
            entry.PropertyName,
            entry.Period ?? IsoDate(entry.Day),
            SourceLabel(entry.Source),
            IsoInstant(entry.ReceivedAt),
            IsoInstant(entry.RetainUntil),
            fileName ?? "file non disponibile",
        ];
    }

    // ---------------------------------------------------------------- coverage report

    /// <summary>The header of the CSV of the report.</summary>
    public static readonly IReadOnlyList<string> ReportHeader =
        ["Obbligo", "Immobile", "Soggiorno", "Arrivo", "Periodo", "Esito", "Origine", "Prova archiviata il (UTC)", "Scadenza", "Cosa fare"];

    /// <summary>The CSV of the report: one row per obligation, the incomplete ones included.</summary>
    public static byte[] Csv(ComplianceCoverageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rows = new List<IReadOnlyList<object?>>();
        rows.Add([.. ReportHeader]);
        rows.AddRange(report.Rows.Select(row => (IReadOnlyList<object?>)
        [
            ItemLabel(row.Kind),
            row.PropertyName,
            row.BookingCode ?? string.Empty,
            row.Day is { } day ? IsoDate(day) : string.Empty,
            row.Period ?? string.Empty,
            Outcome(row),
            Source(row),
            row.ProofAt is { } at ? IsoInstant(at) : string.Empty,
            row.DeadlineAt is { } due ? IsoDate(RomeCalendar.DateInRome(due)) : string.Empty,
            ActionLabel(row.Action),
        ]));
        return CsvWriter.Write(rows, CsvWriter.NoLimit);
    }

    /// <summary>The PDF content of the report: the period, the verdict, the counts, the table of the obligations and the notes.</summary>
    public static PdfDocumentContent Pdf(ComplianceCoverageReport report, string scopeLabel)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeLabel);

        var blocks = new List<PdfBlock>
        {
            new PdfParagraph($"Immobili inclusi: {scopeLabel}"),
            new PdfParagraph($"Periodo: dal {ItalianDate(report.From)} al {ItalianDate(report.To)} (soggiorni con arrivo nel periodo e mesi interi)"),
            new PdfParagraph($"Generato il {ItalianDate(RomeCalendar.DateInRome(report.GeneratedAt))}."),
            new PdfParagraph(
                report.Complete
                    ? "Tutti gli obblighi del periodo hanno la prova archiviata in CasaZen."
                    : "Il rapporto è incompleto: almeno un obbligo del periodo non ha la prova archiviata in CasaZen.",
                Bold: true),
            new PdfParagraph(
                $"Con prova archiviata: {report.Counts.OfficialProof}. {CheckInOctorate}: {report.Counts.ExternalVerificationRequired}. "
                + $"Senza prova: {report.Counts.Missing}. Non dovuti: {report.Counts.NotApplicable}."),
        };

        blocks.Add(new PdfHeading("Obblighi del periodo"));
        if (report.Rows.Count == 0)
        {
            blocks.Add(new PdfParagraph("Nessun obbligo nel periodo."));
        }
        else
        {
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn("Obbligo", 1.4),
                    new PdfTableColumn("Immobile", 1.4),
                    new PdfTableColumn("Soggiorno o periodo", 1.5),
                    new PdfTableColumn("Esito", 1.8),
                    new PdfTableColumn("Origine", 1.1),
                    new PdfTableColumn("Scadenza", 1.0),
                    new PdfTableColumn("Cosa fare", 1.5),
                ],
                report.Rows
                    .Select(row => (IReadOnlyList<string>)
                    [
                        ItemLabel(row.Kind),
                        row.PropertyName,
                        Subject(row),
                        Outcome(row),
                        Source(row),
                        row.DeadlineAt is { } due ? ItalianDate(RomeCalendar.DateInRome(due)) : Dash,
                        ActionLabel(row.Action) is { Length: > 0 } action ? action : Dash,
                    ])
                    .ToList()));
        }

        blocks.Add(new PdfHeading("Note"));
        blocks.Add(new PdfParagraph(
            $"«{CheckInOctorate}»: l'esito di questo obbligo è nell'area collegata di Octorate (origine «{OctorateSource}»). CasaZen non lo conosce e "
            + "non lo deduce: nessuna riga di questo tipo è una prova."));
        blocks.Add(new PdfParagraph(
            "«Prova archiviata»: una ricevuta o un documento conservato da CasaZen per cinque anni. Un avvio del passaggio a Octorate, un ritorno dal "
            + "passaggio o una dichiarazione non sono prove."));
        blocks.Add(new PdfParagraph(NotIssuedByAnAuthority));
        return new PdfDocumentContent("Rapporto di copertura degli adempimenti", blocks);
    }

    /// <summary>The name of the file of a report (<c>rapporto-adempimenti-2026-01-01_2026-03-31.pdf</c>).</summary>
    public static string ReportFileName(ComplianceReportQuery query, ComplianceReportFormat format) =>
        $"rapporto-adempimenti-{IsoDate(query.From)}_{IsoDate(query.To)}{(format == ComplianceReportFormat.Pdf ? ".pdf" : ".csv")}";

    /// <summary>The name of the ZIP of the proofs of a period.</summary>
    public static string ExportFileName(ComplianceArchiveExportQuery query) =>
        $"prove-adempimenti-{IsoDate(query.From)}_{IsoDate(query.To)}.zip";

    // ---------------------------------------------------------------- words

    /// <summary>What an obligation is, in Italian.</summary>
    public static string ItemLabel(ComplianceItemKind kind) => kind switch
    {
        ComplianceItemKind.Alloggiati => "Comunicazione Alloggiati Web",
        ComplianceItemKind.Istat => "Comunicazione ISTAT",
        ComplianceItemKind.TouristTax => "Imposta di soggiorno (dichiarazione e pagamento)",
        _ => kind.ToString(),
    };

    /// <summary>What a kind of proof is, in Italian.</summary>
    public static string KindLabel(ManualProofKind kind) => kind switch
    {
        ManualProofKind.AlloggiatiReceipt => "Ricevuta Alloggiati Web",
        ManualProofKind.IstatProof => "Prova della comunicazione ISTAT",
        ManualProofKind.TouristTaxDeclaration => "Ricevuta della dichiarazione dell'imposta di soggiorno",
        ManualProofKind.TouristTaxPayment => "Ricevuta del pagamento dell'imposta di soggiorno",
        _ => kind.ToString(),
    };

    /// <summary>Where a proof comes from, in Italian.</summary>
    public static string SourceLabel(string? source) => source switch
    {
        ComplianceEvidenceSources.ManualUpload => "Caricata dall'host",
        ComplianceEvidenceSources.CasazenReceipt => "Scaricata da CasaZen",
        ComplianceEvidenceSources.Octorate => OctorateSource,
        _ => Dash,
    };

    /// <summary>The state of a row, in Italian. A row held only by Octorate never says more than <see cref="CheckInOctorate"/>.</summary>
    public static string Outcome(ComplianceReportRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.EvidenceClass switch
        {
            ComplianceEvidenceClass.OfficialProof => "Prova archiviata",
            ComplianceEvidenceClass.ExternalVerificationRequired => CheckInOctorate,
            ComplianceEvidenceClass.NotApplicable => "Non dovuto",
            _ => "Prova mancante",
        };
    }

    private static string Source(ComplianceReportRow row) =>
        row.EvidenceClass == ComplianceEvidenceClass.ExternalVerificationRequired
            ? OctorateSource
            : row.EvidenceClass == ComplianceEvidenceClass.OfficialProof ? SourceLabel(row.EvidenceSource) : Dash;

    private static string Subject(ComplianceReportRow row) =>
        row.BookingCode is not null
            ? $"Soggiorno {row.BookingCode}" + (row.Day is { } day ? $", arrivo {ItalianDate(day)}" : string.Empty)
            : row.Period is not null ? $"Mese {row.Period}" : Dash;

    /// <summary>What the host does to close a row, in Italian; empty when nothing is left to do.</summary>
    public static string ActionLabel(ComplianceCockpitAction? action) => action switch
    {
        ComplianceCockpitAction.SendAlloggiati => "Inviare la comunicazione",
        ComplianceCockpitAction.SendAlloggiatiManually => "Inviare la comunicazione sul portale",
        ComplianceCockpitAction.ResolveAlloggiatiFailure => "Risolvere l'errore dell'invio",
        ComplianceCockpitAction.UploadAlloggiatiReceipt => "Caricare la ricevuta",
        ComplianceCockpitAction.UploadIstatProof => "Caricare la prova della comunicazione",
        ComplianceCockpitAction.DeclareTouristTax => "Dichiarare e versare l'imposta di soggiorno",
        _ => string.Empty,
    };

    private static string IsoDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string ItalianDate(DateOnly date) => date.ToString("dd/MM/yyyy", Italian);

    /// <summary>An instant in UTC, as the index and the report write it: <c>2026-03-14T09:30:00Z</c>.</summary>
    private static string IsoInstant(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
