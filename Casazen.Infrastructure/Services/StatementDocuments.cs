using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The PDF content and the CSV of an owner statement (NF-25, ADR-017, E06/F01/US01/T02), in the style of <see cref="FiscalReportDocuments"/>: the
/// PDF is laid out by <see cref="IPdfDocumentRenderer"/> (A4, the header of a table repeated on every page), the CSV is written by
/// <see cref="CsvWriter"/> (UTF-8 with BOM, <c>;</c>, decimal comma). The labels come from the resource files through <see cref="IDocumentTexts"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Stored, never computed.</b> The lines, the commission, the totals and the withholding are printed as the statement and its evidence store
/// them. Nothing here calculates a withholding: the stored evidence of an authorized payout is printed with its rule version, or the statement
/// says there is none (E06/F05/US02/T03).</item>
/// <item><b>Currency.</b> Every amount is in <see cref="OwnerStatement.Currency"/>, named in the headers; this code never writes a currency.</item>
/// <item><b>Not an invoice.</b> The statement is a management report (E06/F01/US02/T05): it carries no fiscal series or number and says so on
/// top of the first page and in the first row of the CSV.</item>
/// </list>
/// </remarks>
public static class StatementDocuments
{
    public static PdfDocumentContent Pdf(StatementExportData data, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(texts);
        var statement = data.Statement;
        var currency = statement.Currency;
        var blocks = new List<PdfBlock>
        {
            new PdfParagraph(texts.Get("StatementDocNonFiscal", language), Bold: true),
        };

        foreach (var line in HeaderLines(data, texts, language))
            blocks.Add(new PdfParagraph(line));

        blocks.Add(new PdfHeading(texts.Get("StatementDocLinesHeading", language)));
        if (statement.Lines.Count == 0)
        {
            blocks.Add(new PdfParagraph(texts.Get("StatementDocNoLines", language)));
        }
        else
        {
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn(texts.Get("StatementDocColType", language), 1.2),
                    new PdfTableColumn(texts.Get("StatementDocColReference", language), 1.5),
                    Amount(texts.Get("StatementDocColGross", language, currency)),
                    Amount(texts.Get("StatementDocColCommission", language, currency)),
                    Amount(texts.Get("StatementDocColExpenses", language, currency)),
                    Amount(texts.Get("StatementDocColWithholding", language, currency)),
                ],
                OrderedLines(statement)
                    .Select(l => Row(
                        LineTypeLabel(l.Type, texts, language),
                        Reference(l),
                        DocumentLanguages.Money(l.GrossRevenue, language),
                        DocumentLanguages.Money(l.Commission, language),
                        DocumentLanguages.Money(l.Expenses, language),
                        DocumentLanguages.Money(l.Withholding, language)))
                    .ToList()));
        }

        blocks.Add(new PdfHeading(texts.Get("StatementDocTotalsHeading", language)));
        blocks.Add(new PdfTable(
            [
                new PdfTableColumn(texts.Get("StatementDocColItem", language), 2.4),
                Amount(texts.Get("StatementDocColAmount", language, currency)),
            ],
            Totals(statement, texts, language).Select(t => Row(t.Label, DocumentLanguages.Money(t.Amount, language))).ToList()));

        // The ledger the current version was cut from, as stored on it: the statement is a versioned ledger output, and its totals are the ledger's.
        if (data.Ledger is { } ledger)
        {
            blocks.Add(new PdfHeading(texts.Get("StatementDocLedgerHeading", language, ledger.VersionNumber, ledger.Currency)));
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn(texts.Get("StatementDocColItem", language), 2.4),
                    Amount(texts.Get("StatementDocColAmount", language, ledger.Currency)),
                ],
                LedgerTotals(ledger, texts, language).Select(t => Row(t.Label, DocumentLanguages.Money(t.Amount, language))).ToList()));
            blocks.Add(new PdfParagraph(texts.Get("StatementDocLedgerHash", language, ledger.EntriesSha256)));
        }

        blocks.Add(new PdfHeading(texts.Get("StatementDocPayoutsHeading", language)));
        if (data.Payouts.Count == 0)
        {
            blocks.Add(new PdfParagraph(texts.Get("StatementDocPayoutsNone", language)));
        }
        else
        {
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn(texts.Get("StatementDocColPayout", language), 1.6),
                    new PdfTableColumn(texts.Get("StatementDocColDate", language), 1.1),
                    new PdfTableColumn(texts.Get("StatementDocColState", language), 1.1),
                    Amount(texts.Get("StatementDocColAmount", language, currency)),
                ],
                data.Payouts
                    .Select(p => Row(p.Reference, p.PaidOn is { } day ? DocumentLanguages.Day(day) : string.Empty, p.Status, DocumentLanguages.Money(p.Amount, language)))
                    .ToList()));
        }

        blocks.Add(new PdfHeading(texts.Get("StatementDocWithholdingHeading", language)));
        if (data.Withholdings.Count == 0)
        {
            blocks.Add(new PdfParagraph(texts.Get("StatementDocWithholdingNone", language)));
        }
        else
        {
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn(texts.Get("StatementDocColPayout", language), 1.4),
                    new PdfTableColumn(texts.Get("StatementDocColRule", language), 1.5),
                    new PdfTableColumn(texts.Get("StatementDocColDecision", language), 1.1),
                    Amount(texts.Get("StatementDocColBase", language, currency)),
                    Amount(texts.Get("StatementDocColWithholding", language, currency)),
                ],
                data.Withholdings
                    .Select(w => Row(
                        w.PayoutReference,
                        $"{w.RuleCode} v{w.RuleVersion}",
                        DecisionLabel(w.Decision, texts, language),
                        DocumentLanguages.Money(w.GrossBase, language),
                        DocumentLanguages.Money(w.Amount, language)))
                    .ToList()));
        }

        blocks.Add(new PdfParagraph(texts.Get("StatementDocFootnote", language)));

        return new PdfDocumentContent(texts.Get("StatementDocTitle", language, PeriodLabel(statement)), blocks)
        {
            Language = DocumentLanguages.Normalize(language) == DocumentLanguages.English ? "en-GB" : "it-IT",
        };
    }

    public static byte[] Csv(StatementExportData data, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(texts);
        var statement = data.Statement;
        var currency = statement.Currency;

        var rows = new List<IReadOnlyList<object?>>
        {
            Cells(texts.Get("StatementDocNonFiscal", language)),
        };
        rows.AddRange(HeaderLines(data, texts, language).Select(line => Cells(line)));
        rows.Add(Cells(
            texts.Get("StatementDocColType", language),
            texts.Get("StatementDocColReference", language),
            texts.Get("StatementDocColDescription", language),
            texts.Get("StatementDocColCashYear", language),
            texts.Get("StatementDocColGross", language, currency),
            texts.Get("StatementDocColCommission", language, currency),
            texts.Get("StatementDocColExpenses", language, currency),
            texts.Get("StatementDocColWithholding", language, currency)));
        foreach (var line in OrderedLines(statement))
        {
            rows.Add(Cells(
                LineTypeLabel(line.Type, texts, language),
                Reference(line),
                line.Description ?? string.Empty,
                line.CashYear,
                line.GrossRevenue,
                line.Commission,
                line.Expenses,
                line.Withholding));
        }

        foreach (var (label, amount) in Totals(statement, texts, language))
            rows.Add(Cells(label, null, null, null, amount));

        if (data.Ledger is { } ledger)
        {
            rows.Add(Cells(texts.Get("StatementDocLedgerHeading", language, ledger.VersionNumber, ledger.Currency)));
            foreach (var (label, amount) in LedgerTotals(ledger, texts, language))
                rows.Add(Cells(label, null, null, null, amount));
            rows.Add(Cells(texts.Get("StatementDocLedgerHash", language, ledger.EntriesSha256)));
        }

        foreach (var payout in data.Payouts)
        {
            rows.Add(Cells(
                texts.Get("StatementDocColPayout", language),
                payout.Reference,
                payout.Status,
                payout.PaidOn is { } day ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                payout.Amount));
        }

        foreach (var withholding in data.Withholdings)
        {
            rows.Add(Cells(
                texts.Get("StatementDocWithholdingHeading", language),
                withholding.PayoutReference,
                $"{withholding.RuleCode} v{withholding.RuleVersion}",
                DecisionLabel(withholding.Decision, texts, language),
                withholding.GrossBase,
                null,
                null,
                withholding.Amount));
        }

        return CsvWriter.Write(rows, CsvWriter.NoLimit);
    }

    /// <summary>The paragraphs on top of the file: owner, property, period, version, status, share and currency.</summary>
    private static IEnumerable<string> HeaderLines(StatementExportData data, IDocumentTexts texts, string language)
    {
        var statement = data.Statement;
        yield return texts.Get("StatementDocOwner", language, OwnerName(statement));
        yield return texts.Get("StatementDocProperty", language, statement.Property?.Name ?? string.Empty);
        yield return texts.Get("StatementDocPeriod", language, PeriodLabel(statement));
        if (data.VersionNumber is { } version)
            yield return texts.Get("StatementDocVersion", language, version);
        yield return texts.Get("StatementDocStatus", language, StatusLabel(statement.Status, texts, language));
        yield return texts.Get("StatementDocShare", language, statement.OwnershipPercent.ToString("0.##", DocumentLanguages.CultureOf(language)));
        yield return texts.Get("StatementDocCurrency", language, statement.Currency);
    }

    private static IEnumerable<(string Label, decimal Amount)> Totals(OwnerStatement statement, IDocumentTexts texts, string language)
    {
        yield return (texts.Get("StatementDocTotalRevenue", language), statement.TotalRevenue);
        yield return (texts.Get("StatementDocTotalCommission", language), statement.Commission);
        yield return (texts.Get("StatementDocTotalExpenses", language), statement.Expenses);
        yield return (texts.Get("StatementDocTotalWithholding", language), statement.WithholdingTax);
        yield return (texts.Get("StatementDocNetPayable", language), statement.NetPayable);
    }

    /// <summary>The totals of the ledger version as stored, in the currency of the ledger.</summary>
    private static IEnumerable<(string Label, decimal Amount)> LedgerTotals(StatementVersionTotals ledger, IDocumentTexts texts, string language)
    {
        yield return (texts.Get("StatementDocLedgerOpening", language), ledger.OpeningBalance);
        yield return (texts.Get("StatementDocLedgerRevenue", language), ledger.Revenue);
        yield return (texts.Get("StatementDocLedgerChannelFees", language), ledger.ChannelFees);
        yield return (texts.Get("StatementDocLedgerCommission", language), ledger.Commission);
        yield return (texts.Get("StatementDocLedgerCharges", language), ledger.Charges);
        yield return (texts.Get("StatementDocLedgerPayments", language), ledger.Payments);
        yield return (texts.Get("StatementDocLedgerCorrections", language), ledger.Corrections);
        yield return (texts.Get("StatementDocLedgerClosing", language), ledger.ClosingBalance);
    }

    private static IEnumerable<StatementLine> OrderedLines(OwnerStatement statement) =>
        statement.Lines.OrderBy(l => l.Type).ThenBy(l => l.CreatedAt).ThenBy(l => l.Id);

    /// <summary>The booking code of a booking line, else the label the line was given (channel, expense category, reason).</summary>
    private static string Reference(StatementLine line) =>
        line.Booking is { } booking ? BookingCodes.Format(booking.BookingCode) : line.Label ?? string.Empty;

    private static string OwnerName(OwnerStatement statement) =>
        statement.PropertyOwner is { } owner
            ? (string.IsNullOrWhiteSpace(owner.LegalName) ? $"{owner.FirstName} {owner.LastName}".Trim() : owner.LegalName.Trim())
            : string.Empty;

    private static string PeriodLabel(OwnerStatement statement) =>
        $"{statement.Month.ToString("00", CultureInfo.InvariantCulture)}/{statement.Year.ToString(CultureInfo.InvariantCulture)}";

    private static string LineTypeLabel(OwnerStatementLineType type, IDocumentTexts texts, string language) => type switch
    {
        OwnerStatementLineType.Booking => texts.Get("StatementDocLineBooking", language),
        OwnerStatementLineType.OtaRevenue => texts.Get("StatementDocLineOtaRevenue", language),
        OwnerStatementLineType.Expense => texts.Get("StatementDocLineExpense", language),
        _ => texts.Get("StatementDocLineAdjustment", language),
    };

    private static string StatusLabel(StatementStatus status, IDocumentTexts texts, string language) => status switch
    {
        StatementStatus.Published => texts.Get("StatementDocStatusPublished", language),
        StatementStatus.Approved => texts.Get("StatementDocStatusApproved", language),
        _ => texts.Get("StatementDocStatusDraft", language),
    };

    private static string DecisionLabel(string decision, IDocumentTexts texts, string language) => decision switch
    {
        "applicable" => texts.Get("StatementDocDecisionApplicable", language),
        "not_applicable" => texts.Get("StatementDocDecisionNotApplicable", language),
        _ => decision,
    };

    private static PdfTableColumn Amount(string header) => new(header, 1.1, PdfCellAlignment.Right);

    private static IReadOnlyList<string> Row(params string[] cells) => cells;

    private static IReadOnlyList<object?> Cells(params object?[] cells) => cells;
}
