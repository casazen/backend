using System.Globalization;
using Casazen.Core.Documents;

namespace Casazen.Core.Condominium;

/// <summary>The two PDFs CondoLedger owners send, plus the same figures the screen shows.</summary>
public static class SituazioneDocuments
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static PdfDocumentContent Paid(SituazioneReport.Year year) =>
        Document(year, "Spese già pagate", year.Payments.Count == 0
            ? [new PdfParagraph("Nessun versamento in questo esercizio.")]
            : PaymentBlocks(year));

    public static PdfDocumentContent Unpaid(SituazioneReport.Year year)
    {
        var blocks = new List<PdfBlock>
        {
            new PdfParagraph($"Ancora da versare: {Money(year.StillToPay)}"),
        };
        var open = year.Installments.Where(row => row.IsOpen).ToList();
        if (open.Count == 0)
        {
            blocks.Add(new PdfParagraph("Nessuna rata scoperta."));
        }
        else
        {
            blocks.Add(new PdfHeading("Rate scoperte"));
            blocks.Add(Table(
                open.Select(row => (IReadOnlyList<string>)
                [
                    row.Label,
                    Date(row.DueDate),
                    Money(row.Ordinary),
                    Money(row.Prior),
                    Money(row.Extraordinary),
                    Money(row.Remaining),
                ]).ToList()));
        }

        if (year.Settlement is { } settlement)
        {
            blocks.Add(new PdfParagraph(
                $"Conguaglio: {Money(settlement.Amount)} (consuntivo {Money(settlement.Consuntivo)} + straordinari {Money(settlement.Straordinari)} + saldo precedente {Money(settlement.Riportato)} − versato {Money(settlement.Pagato)})."));
        }

        return Document(year, "Spese ancora da pagare", blocks);
    }

    private static PdfDocumentContent Document(SituazioneReport.Year year, string title, IReadOnlyList<PdfBlock> body)
    {
        var blocks = new List<PdfBlock>
        {
            new PdfParagraph($"{year.CondominiumName}"),
            new PdfParagraph($"Esercizio {year.PeriodLabel} ({Date(year.PeriodStart)} – {Date(year.PeriodEnd)})"),
            new PdfParagraph($"Preventivo {Money(year.Ordinary)} · Straordinari {Money(year.Extraordinary)} · Saldo precedente {Money(year.Prior)} · Versato {Money(year.Paid)}"),
        };
        blocks.AddRange(body);
        return new PdfDocumentContent(title, blocks) { Language = "it-IT" };
    }

    private static IReadOnlyList<PdfBlock> PaymentBlocks(SituazioneReport.Year year)
    {
        var rows = year.Payments
            .OrderByDescending(payment => payment.PaidOn)
            .Select(payment => (IReadOnlyList<string>)
            [
                payment.Covers,
                Date(payment.PaidOn),
                payment.Method ?? "—",
                Money(payment.Amount),
            ])
            .ToList();
        rows.Add(["Totale versato", string.Empty, string.Empty, Money(year.Paid)]);
        return
        [
            new PdfHeading("Versamenti"),
            new PdfTable(
                [
                    new PdfTableColumn("A copertura di", 1.6),
                    new PdfTableColumn("Data", 1),
                    new PdfTableColumn("Metodo", 1),
                    new PdfTableColumn("Importo", 1, PdfCellAlignment.Right),
                ],
                rows),
        ];
    }

    private static PdfTable Table(IReadOnlyList<IReadOnlyList<string>> rows) => new(
        [
            new PdfTableColumn("Rata", 1.4),
            new PdfTableColumn("Scadenza", 1),
            new PdfTableColumn("Ordinario", 1, PdfCellAlignment.Right),
            new PdfTableColumn("Saldo prec.", 1, PdfCellAlignment.Right),
            new PdfTableColumn("Straord.", 1, PdfCellAlignment.Right),
            new PdfTableColumn("Residuo", 1, PdfCellAlignment.Right),
        ],
        rows);

    private static string Money(decimal amount) => amount.ToString("N2", Italian);

    private static string Date(DateOnly day) => day.ToString("dd/MM/yyyy", Italian);
}
