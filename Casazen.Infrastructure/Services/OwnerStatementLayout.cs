using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The layout of the statement of an owner (E13/F01/US02/T01, NF-110, NF-115), pure: from an <see cref="OwnerStatementContent"/> it makes the blocks the PDF
/// renderer lays out and the rows of the CSV, and it groups the ledger lines of a version into the parts of the file. It reads no database and calls no service.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The parts, in this order:</b> «Soggiorni» (the gross of each stay without the tourist tax), «Commissioni dei canali» (labelled by who holds the portal
/// account of the listing: <c>V1</c> the owner, <c>V2</c> the agency), «Ritenuta» (only when present), «Provvigione del PM» (with the number of the owner invoice
/// when one is linked), «Riaddebiti e spese», «Rettifiche» (only when present) and «Netto» (the opening balance, the total of the period, the payments and the
/// closing balance). The box «Tassa di soggiorno» follows, for information only: it is never added to or taken from the net.</item>
/// <item><b>Stored, never recomputed.</b> The amounts are the signed amounts of the ledger lines, the balances the ones the version stores; nothing here works a
/// commission, a fee or a tax out.</item>
/// <item><b>Money.</b> The PDF writes amounts in the number format of the currency of the statement: <c>it-CH</c> (<c>1'234.50</c>) for the Swiss franc,
/// <c>it-IT</c> (<c>1.234,50</c>) for any other; the currency is in the headers. The CSV is written by <see cref="CsvWriter"/> (a decimal comma and no thousands
/// separator, the currency in a column of its own).</item>
/// </list>
/// </remarks>
public static class OwnerStatementLayout
{
    /// <summary>The figures of a Swiss statement: the it-CH culture with the apostrophe as the thousands separator and the point as the decimal one, pinned so every platform writes the same.</summary>
    private static readonly NumberFormatInfo SwissNumbers = CreateSwissNumbers();

    private static readonly NumberFormatInfo ItalianNumbers = CultureInfo.GetCultureInfo("it-IT").NumberFormat;

    /// <summary>The parts of the statement in the order of the file.</summary>
    public static IReadOnlyList<OwnerStatementSection> Sections { get; } = Enum.GetValues<OwnerStatementSection>();

    /// <summary>Every key of <c>SharedResources</c> the layout reads, so a test can check each exists in both languages.</summary>
    public static IReadOnlyList<string> TextKeys { get; } =
    [
        "StatementFileTitle", "StatementFileDocumentTitle", "StatementFileAgency", "StatementFileOwner", "StatementFilePeriod", "StatementFileVersion",
        "StatementFileCurrency", "StatementFilePublished", "StatementFileDraft", "StatementFileWatermarkDraft", "StatementFileNotInvoice",
        "StatementFileNone", "StatementFileTotal", "StatementFileStaysHeading", "StatementFileChannelFeesHeading", "StatementFileWithholdingHeading",
        "StatementFileCommissionHeading", "StatementFileRechargesHeading", "StatementFileCorrectionsHeading", "StatementFileNetHeading",
        "StatementFilePaymentsHeading", "StatementFileTouristTaxHeading", "StatementFileTouristTaxNote", "StatementFileTouristTaxNone",
        "StatementFileColProperty", "StatementFileColStay", "StatementFileColChannel", "StatementFileColShare", "StatementFileColDate",
        "StatementFileColDescription", "StatementFileColAmount", "StatementFileColAmountPlain", "StatementFileColGross", "StatementFileColItem", "StatementFileColSection",
        "StatementFileChannelFeesOwner", "StatementFileChannelFeesAgency", "StatementFileChannelFeesAgencyInvoice", "StatementFileCommissionInvoice",
        "StatementFileRechargeCleaning", "StatementFileRechargeLinen", "StatementFileRechargeCardFee", "StatementFileRechargeCharge",
        "StatementFileRechargeExpense", "StatementFileWithholdingLine", "StatementFileCorrectionLine", "StatementFilePaymentToOwner",
        "StatementFilePaymentFromOwner", "StatementFileReversal", "StatementFileNetOpening", "StatementFileNetPeriod", "StatementFileNetPayments",
        "StatementFileNetClosing", "StatementFileLinesHash", "StatementFileChannelDirect", "StatementFileChannelManual", "StatementFileChannelOther",
    ];

    /// <summary>The part a ledger line is in. A type nobody mapped is an error (a test lists every type), never a line shown under another part.</summary>
    public static OwnerStatementSection SectionOf(OwnerLedgerEntryType type)
    {
        return type switch
        {
            OwnerLedgerEntryType.BookingRevenue => OwnerStatementSection.Stays,
            OwnerLedgerEntryType.ChannelFee => OwnerStatementSection.ChannelFees,
            OwnerLedgerEntryType.Withholding => OwnerStatementSection.Withholding,
            OwnerLedgerEntryType.AgencyCommission => OwnerStatementSection.Commission,
            OwnerLedgerEntryType.OwnerCharge or OwnerLedgerEntryType.OwnerExpense => OwnerStatementSection.Recharges,
            OwnerLedgerEntryType.Correction => OwnerStatementSection.Corrections,
            OwnerLedgerEntryType.OwnerPayment or OwnerLedgerEntryType.AgencyPayment => OwnerStatementSection.Payments,
            _ => throw new InvalidOperationException($"The ledger line type {type} has no part in the owner statement."),
        };
    }

    /// <summary>The totals of the lines of a version: each part, the total of the period, the payments, and the balances (the opening one given, the closing one worked out).</summary>
    public static OwnerStatementTotals TotalsOf(decimal openingBalance, IReadOnlyList<OwnerStatementLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        decimal Sum(OwnerStatementSection section) => lines.Where(line => line.Section == section).Sum(line => line.Amount);

        var gross = Sum(OwnerStatementSection.Stays);
        var channelFees = Sum(OwnerStatementSection.ChannelFees);
        var withholding = Sum(OwnerStatementSection.Withholding);
        var commission = Sum(OwnerStatementSection.Commission);
        var recharges = Sum(OwnerStatementSection.Recharges);
        var corrections = Sum(OwnerStatementSection.Corrections);
        var payments = Sum(OwnerStatementSection.Payments);
        var period = gross + channelFees + withholding + commission + recharges + corrections;
        return new OwnerStatementTotals(
            gross, channelFees, withholding, commission, recharges, corrections, period, payments, openingBalance, openingBalance + period + payments);
    }

    /// <summary>An amount to two places in the number format of <paramref name="currency"/> (Swiss for CHF, Italian otherwise); the currency is written by the caller.</summary>
    public static string Money(decimal amount, string? currency)
    {
        var numbers = string.Equals(currency, StayCurrency.SwissFranc, StringComparison.OrdinalIgnoreCase) ? SwissNumbers : ItalianNumbers;
        return amount.ToString("N2", numbers);
    }

    /// <summary>The PDF of the statement: the blocks <see cref="IPdfDocumentRenderer"/> lays out. A version that is not published carries the watermark of a draft.</summary>
    public static PdfDocumentContent Pdf(OwnerStatementContent content, IDocumentTexts texts)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(texts);

        var language = content.Language;
        var currency = content.Currency;
        var period = PeriodLabel(content);
        var blocks = new List<PdfBlock>
        {
            new PdfParagraph(texts.Get("StatementFileAgency", language, content.AgencyName)),
            new PdfParagraph(texts.Get("StatementFileOwner", language, content.OwnerName)),
            new PdfParagraph(texts.Get("StatementFilePeriod", language, period)),
            new PdfParagraph(texts.Get("StatementFileVersion", language, content.VersionNumber)),
            new PdfParagraph(texts.Get("StatementFileCurrency", language, currency)),
            new PdfParagraph(content.PublishedAt is { } published
                ? texts.Get("StatementFilePublished", language, DocumentLanguages.Day(DateOnly.FromDateTime(published)))
                : texts.Get("StatementFileDraft", language)),
            new PdfParagraph(texts.Get("StatementFileNotInvoice", language)),
        };

        var amount = Amount(texts.Get("StatementFileColAmount", language, currency));
        AddStays(blocks, content, texts);
        AddChannelFees(blocks, content, texts, amount);
        AddWithholding(blocks, content, texts, amount);
        AddCommission(blocks, content, texts, amount);
        AddRecharges(blocks, content, texts, amount);
        AddCorrections(blocks, content, texts, amount);
        AddNet(blocks, content, texts);
        AddTouristTax(blocks, content, texts);
        blocks.Add(new PdfParagraph(texts.Get("StatementFileLinesHash", language, content.EntriesSha256)));

        return new PdfDocumentContent(texts.Get("StatementFileTitle", language, period), blocks)
        {
            Language = LanguageTag(language, currency),
            Watermark = content.PublishedAt is null ? texts.Get("StatementFileWatermarkDraft", language) : null,
        };
    }

    /// <summary>The CSV of the statement: the header, one row for every line of every part, the totals of each part, the net and the tourist tax box (outside the net).</summary>
    public static byte[] Csv(OwnerStatementContent content, IDocumentTexts texts)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(texts);

        var language = content.Language;
        var currency = content.Currency;
        var totals = content.Totals;
        var rows = new List<IReadOnlyList<object?>>
        {
            Cells(texts.Get("StatementFileTitle", language, PeriodLabel(content))),
            Cells(texts.Get("StatementFileAgency", language, content.AgencyName)),
            Cells(texts.Get("StatementFileOwner", language, content.OwnerName)),
            Cells(texts.Get("StatementFileVersion", language, content.VersionNumber)),
            Cells(texts.Get("StatementFileCurrency", language, currency)),
            Cells(content.PublishedAt is { } published
                ? texts.Get("StatementFilePublished", language, DocumentLanguages.Day(DateOnly.FromDateTime(published)))
                : texts.Get("StatementFileDraft", language)),
            Cells(texts.Get("StatementFileNotInvoice", language)),
            Cells(
                texts.Get("StatementFileColSection", language),
                texts.Get("StatementFileColDate", language),
                texts.Get("StatementFileColProperty", language),
                texts.Get("StatementFileColStay", language),
                texts.Get("StatementFileColChannel", language),
                texts.Get("StatementFileColDescription", language),
                texts.Get("StatementFileColShare", language),
                texts.Get("StatementFileColAmount", language, currency),
                "ISO 4217"),
        };

        foreach (var section in new[]
                 {
                     OwnerStatementSection.Stays, OwnerStatementSection.ChannelFees, OwnerStatementSection.Withholding, OwnerStatementSection.Commission,
                     OwnerStatementSection.Recharges, OwnerStatementSection.Corrections, OwnerStatementSection.Payments,
                 })
        {
            var lines = content.Of(section);
            if (lines.Count == 0 && section is OwnerStatementSection.Withholding or OwnerStatementSection.Corrections or OwnerStatementSection.Payments)
                continue;

            var heading = SectionHeading(section, texts, language);
            foreach (var line in lines)
            {
                rows.Add(Cells(
                    heading,
                    line.Date,
                    line.PropertyName,
                    StayLabel(line, texts, language),
                    ChannelLabel(line.Channel, texts, language),
                    DescriptionOf(line, content, texts),
                    line.SharePercent,
                    line.Amount,
                    currency));
            }

            rows.Add(Cells(texts.Get("StatementFileTotal", language) + " - " + heading, null, null, null, null, null, null, lines.Sum(line => line.Amount), currency));
        }

        rows.Add(Cells(texts.Get("StatementFileNetOpening", language), null, null, null, null, null, null, totals.OpeningBalance, currency));
        rows.Add(Cells(texts.Get("StatementFileNetPeriod", language), null, null, null, null, null, null, totals.Period, currency));
        rows.Add(Cells(texts.Get("StatementFileNetPayments", language), null, null, null, null, null, null, totals.Payments, currency));
        rows.Add(Cells(texts.Get("StatementFileNetClosing", language), null, null, null, null, null, null, totals.ClosingBalance, currency));

        // The tourist tax: for information only, in a part of its own with its own currency, never in a total above.
        var box = texts.Get("StatementFileTouristTaxHeading", language);
        foreach (var tax in content.TouristTax)
        {
            rows.Add(Cells(
                box,
                null,
                tax.PropertyName,
                StayLabel(tax.CheckIn, tax.CheckOut, tax.BookingCode),
                ChannelLabel(tax.Channel, texts, language),
                null,
                null,
                tax.Amount,
                tax.Currency));
        }

        rows.Add(Cells(texts.Get("StatementFileLinesHash", language, content.EntriesSha256)));
        return CsvWriter.Write(rows, CsvWriter.NoLimit);
    }

    // ---------------------------------------------------------------- the parts of the PDF

    private static void AddStays(List<PdfBlock> blocks, OwnerStatementContent content, IDocumentTexts texts)
    {
        var language = content.Language;
        blocks.Add(new PdfHeading(texts.Get("StatementFileStaysHeading", language)));
        var lines = content.Of(OwnerStatementSection.Stays);
        if (lines.Count == 0)
        {
            blocks.Add(new PdfParagraph(texts.Get("StatementFileNone", language)));
            return;
        }

        var gross = Amount(texts.Get("StatementFileColGross", language, content.Currency));
        var rows = lines
            .Select(line => Row(
                line.PropertyName ?? string.Empty,
                StayLabel(line, texts, language),
                ChannelLabel(line.Channel, texts, language),
                Share(line.SharePercent),
                Money(line.Amount, content.Currency)))
            .ToList();
        rows.Add(Row(texts.Get("StatementFileTotal", language), string.Empty, string.Empty, string.Empty, Money(lines.Sum(line => line.Amount), content.Currency)));
        blocks.Add(new PdfTable(
            [
                new PdfTableColumn(texts.Get("StatementFileColProperty", language), 1.5),
                new PdfTableColumn(texts.Get("StatementFileColStay", language), 1.8),
                new PdfTableColumn(texts.Get("StatementFileColChannel", language), 1.0),
                new PdfTableColumn(texts.Get("StatementFileColShare", language), 0.6, PdfCellAlignment.Right),
                gross with { RelativeWidth = 1.3 },
            ],
            rows));
    }

    private static void AddChannelFees(List<PdfBlock> blocks, OwnerStatementContent content, IDocumentTexts texts, PdfTableColumn amount)
    {
        var language = content.Language;
        blocks.Add(new PdfHeading(texts.Get("StatementFileChannelFeesHeading", language)));
        var lines = content.Of(OwnerStatementSection.ChannelFees);
        if (lines.Count == 0)
        {
            blocks.Add(new PdfParagraph(texts.Get("StatementFileNone", language)));
            return;
        }

        // The label follows who holds the portal account of the listing: the portal bills the owner (V1) or the agency, which recharges it (V2).
        foreach (var heldByAgency in new[] { false, true })
        {
            var group = lines.Where(line => line.AgencyHoldsPortalAccount == heldByAgency).ToList();
            if (group.Count == 0)
                continue;

            blocks.Add(new PdfParagraph(
                !heldByAgency ? texts.Get("StatementFileChannelFeesOwner", language)
                : content.OwnerInvoiceNumber is { } number ? texts.Get("StatementFileChannelFeesAgencyInvoice", language, number)
                : texts.Get("StatementFileChannelFeesAgency", language),
                Bold: true));
            var rows = group
                .Select(line => Row(line.PropertyName ?? string.Empty, StayLabel(line, texts, language), ChannelLabel(line.Channel, texts, language), Money(line.Amount, content.Currency)))
                .ToList();
            rows.Add(Row(texts.Get("StatementFileTotal", language), string.Empty, string.Empty, Money(group.Sum(line => line.Amount), content.Currency)));
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn(texts.Get("StatementFileColProperty", language), 1.5),
                    new PdfTableColumn(texts.Get("StatementFileColStay", language), 1.8),
                    new PdfTableColumn(texts.Get("StatementFileColChannel", language), 1.0),
                    amount,
                ],
                rows));
        }
    }

    private static void AddWithholding(List<PdfBlock> blocks, OwnerStatementContent content, IDocumentTexts texts, PdfTableColumn amount)
    {
        var lines = content.Of(OwnerStatementSection.Withholding);
        if (lines.Count == 0)
            return;

        var language = content.Language;
        blocks.Add(new PdfHeading(texts.Get("StatementFileWithholdingHeading", language)));
        var rows = lines
            .Select(line => Row(DocumentLanguages.Day(line.Date), DescriptionOf(line, content, texts), Money(line.Amount, content.Currency)))
            .ToList();
        rows.Add(Row(texts.Get("StatementFileTotal", language), string.Empty, Money(lines.Sum(line => line.Amount), content.Currency)));
        blocks.Add(new PdfTable(
            [
                new PdfTableColumn(texts.Get("StatementFileColDate", language), 1.0),
                new PdfTableColumn(texts.Get("StatementFileColDescription", language), 2.6),
                amount,
            ],
            rows));
    }

    private static void AddCommission(List<PdfBlock> blocks, OwnerStatementContent content, IDocumentTexts texts, PdfTableColumn amount)
    {
        var language = content.Language;
        blocks.Add(new PdfHeading(texts.Get("StatementFileCommissionHeading", language)));
        if (content.OwnerInvoiceNumber is { } number)
        {
            var issued = content.OwnerInvoiceDate is { } day ? $" ({DocumentLanguages.Day(day)})" : string.Empty;
            blocks.Add(new PdfParagraph(texts.Get("StatementFileCommissionInvoice", language, number) + issued, Bold: true));
        }

        var lines = content.Of(OwnerStatementSection.Commission);
        if (lines.Count == 0)
        {
            blocks.Add(new PdfParagraph(texts.Get("StatementFileNone", language)));
            return;
        }

        var rows = lines
            .Select(line => Row(line.PropertyName ?? string.Empty, StayLabel(line, texts, language), Money(line.Amount, content.Currency)))
            .ToList();
        rows.Add(Row(texts.Get("StatementFileTotal", language), string.Empty, Money(lines.Sum(line => line.Amount), content.Currency)));
        blocks.Add(new PdfTable(
            [
                new PdfTableColumn(texts.Get("StatementFileColProperty", language), 1.5),
                new PdfTableColumn(texts.Get("StatementFileColStay", language), 2.0),
                amount,
            ],
            rows));
    }

    private static void AddRecharges(List<PdfBlock> blocks, OwnerStatementContent content, IDocumentTexts texts, PdfTableColumn amount)
    {
        var language = content.Language;
        blocks.Add(new PdfHeading(texts.Get("StatementFileRechargesHeading", language)));
        var lines = content.Of(OwnerStatementSection.Recharges);
        if (lines.Count == 0)
        {
            blocks.Add(new PdfParagraph(texts.Get("StatementFileNone", language)));
            return;
        }

        var rows = lines
            .Select(line => Row(DescriptionOf(line, content, texts), line.PropertyName ?? string.Empty, StayLabel(line, texts, language), Money(line.Amount, content.Currency)))
            .ToList();
        rows.Add(Row(texts.Get("StatementFileTotal", language), string.Empty, string.Empty, Money(lines.Sum(line => line.Amount), content.Currency)));
        blocks.Add(new PdfTable(
            [
                new PdfTableColumn(texts.Get("StatementFileColDescription", language), 1.8),
                new PdfTableColumn(texts.Get("StatementFileColProperty", language), 1.4),
                new PdfTableColumn(texts.Get("StatementFileColStay", language), 1.6),
                amount,
            ],
            rows));
    }

    private static void AddCorrections(List<PdfBlock> blocks, OwnerStatementContent content, IDocumentTexts texts, PdfTableColumn amount)
    {
        var lines = content.Of(OwnerStatementSection.Corrections);
        if (lines.Count == 0)
            return;

        var language = content.Language;
        blocks.Add(new PdfHeading(texts.Get("StatementFileCorrectionsHeading", language)));
        var rows = lines
            .Select(line => Row(DocumentLanguages.Day(line.Date), DescriptionOf(line, content, texts), Money(line.Amount, content.Currency)))
            .ToList();
        rows.Add(Row(texts.Get("StatementFileTotal", language), string.Empty, Money(lines.Sum(line => line.Amount), content.Currency)));
        blocks.Add(new PdfTable(
            [
                new PdfTableColumn(texts.Get("StatementFileColDate", language), 1.0),
                new PdfTableColumn(texts.Get("StatementFileColDescription", language), 2.6),
                amount,
            ],
            rows));
    }

    /// <summary>«Netto»: the payments, then the opening balance, the total of the period, the payments and the closing balance, the ones the version stores.</summary>
    private static void AddNet(List<PdfBlock> blocks, OwnerStatementContent content, IDocumentTexts texts)
    {
        var language = content.Language;
        var totals = content.Totals;
        blocks.Add(new PdfHeading(texts.Get("StatementFileNetHeading", language)));

        var payments = content.Of(OwnerStatementSection.Payments);
        if (payments.Count > 0)
        {
            blocks.Add(new PdfParagraph(texts.Get("StatementFilePaymentsHeading", language), Bold: true));
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn(texts.Get("StatementFileColDate", language), 1.0),
                    new PdfTableColumn(texts.Get("StatementFileColDescription", language), 2.6),
                    Amount(texts.Get("StatementFileColAmount", language, content.Currency)),
                ],
                payments.Select(line => Row(DocumentLanguages.Day(line.Date), DescriptionOf(line, content, texts), Money(line.Amount, content.Currency))).ToList()));
        }

        blocks.Add(new PdfTable(
            [
                new PdfTableColumn(texts.Get("StatementFileColItem", language), 2.4),
                Amount(texts.Get("StatementFileColAmount", language, content.Currency)),
            ],
            [
                Row(texts.Get("StatementFileNetOpening", language), Money(totals.OpeningBalance, content.Currency)),
                Row(texts.Get("StatementFileNetPeriod", language), Money(totals.Period, content.Currency)),
                Row(texts.Get("StatementFileNetPayments", language), Money(totals.Payments, content.Currency)),
                Row(texts.Get("StatementFileNetClosing", language), Money(totals.ClosingBalance, content.Currency)),
            ]));
    }

    /// <summary>The box «Tassa di soggiorno»: collected by the agency, to be remitted; for information only and outside the net.</summary>
    private static void AddTouristTax(List<PdfBlock> blocks, OwnerStatementContent content, IDocumentTexts texts)
    {
        var language = content.Language;
        blocks.Add(new PdfHeading(texts.Get("StatementFileTouristTaxHeading", language)));
        blocks.Add(new PdfParagraph(texts.Get("StatementFileTouristTaxNote", language)));
        if (content.TouristTax.Count == 0)
        {
            blocks.Add(new PdfParagraph(texts.Get("StatementFileTouristTaxNone", language)));
            return;
        }

        var rows = content.TouristTax
            .Select(tax => Row(
                tax.PropertyName ?? string.Empty,
                StayLabel(tax.CheckIn, tax.CheckOut, tax.BookingCode),
                ChannelLabel(tax.Channel, texts, language),
                tax.Currency + " " + Money(tax.Amount, tax.Currency)))
            .ToList();
        foreach (var currency in content.TouristTax.GroupBy(tax => tax.Currency, StringComparer.Ordinal))
            rows.Add(Row(texts.Get("StatementFileTotal", language), string.Empty, string.Empty, currency.Key + " " + Money(currency.Sum(tax => tax.Amount), currency.Key)));

        blocks.Add(new PdfTable(
            [
                new PdfTableColumn(texts.Get("StatementFileColProperty", language), 1.5),
                new PdfTableColumn(texts.Get("StatementFileColStay", language), 1.8),
                new PdfTableColumn(texts.Get("StatementFileColChannel", language), 1.0),
                new PdfTableColumn(texts.Get("StatementFileColAmountPlain", language), 1.2, PdfCellAlignment.Right),
            ],
            rows));
    }

    // ---------------------------------------------------------------- labels

    /// <summary>The days of a stay (<c>dd/MM/yyyy - dd/MM/yyyy</c>) with the code of its booking; the day of the line for a line that is not of a stay, and nothing for a line with no day.</summary>
    private static string StayLabel(OwnerStatementLine line, IDocumentTexts texts, string language)
    {
        var days = line.CheckIn is { } checkIn && line.CheckOut is { } checkOut
            ? DocumentLanguages.Day(checkIn) + " - " + DocumentLanguages.Day(checkOut)
            : line.CheckIn is null && line.BookingCode is null ? DocumentLanguages.Day(line.Date) : string.Empty;
        var code = line.BookingCode is { Length: > 0 } booking ? $" ({booking})" : string.Empty;
        var reversal = line.Reversal ? " - " + texts.Get("StatementFileReversal", language) : string.Empty;
        return (days + code + reversal).Trim();
    }

    private static string StayLabel(DateOnly checkIn, DateOnly checkOut, string? bookingCode)
    {
        var days = DocumentLanguages.Day(checkIn) + " - " + DocumentLanguages.Day(checkOut);
        return bookingCode is { Length: > 0 } ? $"{days} ({bookingCode})" : days;
    }

    /// <summary>The words of a line: the agency's own for a charge, an expense or a correction; the kind of a flat fee; a label for what has none.</summary>
    private static string DescriptionOf(OwnerStatementLine line, OwnerStatementContent content, IDocumentTexts texts)
    {
        var language = content.Language;
        var text = line.Type switch
        {
            OwnerLedgerEntryType.Withholding => line.Description is { Length: > 0 } ? line.Description : texts.Get("StatementFileWithholdingLine", language),
            OwnerLedgerEntryType.Correction => line.Description is { Length: > 0 } ? line.Description : texts.Get("StatementFileCorrectionLine", language),
            OwnerLedgerEntryType.OwnerPayment => line.Description is { Length: > 0 } ? line.Description : texts.Get("StatementFilePaymentToOwner", language),
            OwnerLedgerEntryType.AgencyPayment => line.Description is { Length: > 0 } ? line.Description : texts.Get("StatementFilePaymentFromOwner", language),
            OwnerLedgerEntryType.OwnerCharge or OwnerLedgerEntryType.OwnerExpense => RechargeLabel(line, texts, language),
            _ => line.Description ?? string.Empty,
        };
        return line.Reversal ? $"{texts.Get("StatementFileReversal", language)}: {text}" : text;
    }

    private static string RechargeLabel(OwnerStatementLine line, IDocumentTexts texts, string language)
    {
        if (line.Description is { Length: > 0 } words)
            return words;

        return line.Reference switch
        {
            OwnerLedgerFlatFees.Cleaning => texts.Get("StatementFileRechargeCleaning", language),
            OwnerLedgerFlatFees.Linen => texts.Get("StatementFileRechargeLinen", language),
            _ when line.Type == OwnerLedgerEntryType.OwnerExpense && line.BookingCode is not null => texts.Get("StatementFileRechargeCardFee", language),
            _ when line.Type == OwnerLedgerEntryType.OwnerExpense => texts.Get("StatementFileRechargeExpense", language),
            _ => texts.Get("StatementFileRechargeCharge", language),
        };
    }

    /// <summary>The channel of a stay as the file writes it: a portal by its name, the generic ones by a label in the language of the file.</summary>
    private static string ChannelLabel(string? channel, IDocumentTexts texts, string language)
    {
        return channel switch
        {
            null or "" => string.Empty,
            nameof(BookingSource.Direct) => texts.Get("StatementFileChannelDirect", language),
            nameof(BookingSource.Manual) or nameof(BookingSource.Local) => texts.Get("StatementFileChannelManual", language),
            nameof(BookingSource.OtherChannel) => texts.Get("StatementFileChannelOther", language),
            nameof(BookingSource.BookingCom) => "Booking.com",
            _ => channel,
        };
    }

    private static string SectionHeading(OwnerStatementSection section, IDocumentTexts texts, string language)
    {
        return section switch
        {
            OwnerStatementSection.Stays => texts.Get("StatementFileStaysHeading", language),
            OwnerStatementSection.ChannelFees => texts.Get("StatementFileChannelFeesHeading", language),
            OwnerStatementSection.Withholding => texts.Get("StatementFileWithholdingHeading", language),
            OwnerStatementSection.Commission => texts.Get("StatementFileCommissionHeading", language),
            OwnerStatementSection.Recharges => texts.Get("StatementFileRechargesHeading", language),
            OwnerStatementSection.Corrections => texts.Get("StatementFileCorrectionsHeading", language),
            OwnerStatementSection.Payments => texts.Get("StatementFilePaymentsHeading", language),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Not a part of the owner statement."),
        };
    }

    /// <summary>The language of the PDF catalog: English, or Italian as written in the country of the currency (Switzerland for the franc).</summary>
    private static string LanguageTag(string language, string currency)
    {
        if (DocumentLanguages.Normalize(language) == DocumentLanguages.English)
            return "en-GB";

        return string.Equals(currency, StayCurrency.SwissFranc, StringComparison.OrdinalIgnoreCase) ? "it-CH" : "it-IT";
    }

    private static string PeriodLabel(OwnerStatementContent content) =>
        string.Create(CultureInfo.InvariantCulture, $"{content.Month:00}/{content.Year:0000}");

    private static string Share(decimal percent) => percent == 100m ? "100%" : percent.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static PdfTableColumn Amount(string header) => new(header, 1.2, PdfCellAlignment.Right);

    private static IReadOnlyList<string> Row(params string[] cells) => cells;

    private static IReadOnlyList<object?> Cells(params object?[] cells) => cells;

    private static NumberFormatInfo CreateSwissNumbers()
    {
        var numbers = (NumberFormatInfo)CultureInfo.GetCultureInfo("it-CH").NumberFormat.Clone();
        numbers.NumberGroupSeparator = "'";
        numbers.NumberDecimalSeparator = ".";
        numbers.NumberGroupSizes = [3];
        return NumberFormatInfo.ReadOnly(numbers);
    }
}
