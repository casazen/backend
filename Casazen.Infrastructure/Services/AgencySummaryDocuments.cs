using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The CSV, the Excel file and the PDF of the agency summary (E15/F01/US01/T01, acceptance steps C7 and D3): the one set of tables built from an <see cref="AgencySummary"/>, so the
/// three files and the JSON of the screen carry the same figures. The words come from the resource files (<see cref="PackageTexts"/>); an amount is a number to the cent in the
/// currency of its own column. The owner tables are always there, only their header when the caller may not read the owner accounting, and a table says why they are empty.
/// </summary>
internal static class AgencySummaryDocuments
{
    /// <summary>The summary as tables, in the order of the file: the period, the stays, the money, the documents, the costs of the channels, the tourist tax, then the owner accounting.</summary>
    public static IReadOnlyList<ReportTable> Tables(AgencySummary summary, PackageTexts t)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(t);

        var currency = t["FiscalExportColCurrency"];
        var tables = new List<ReportTable>();

        var head = new ReportTable(t.Get(PackageLabels.SummaryTitle, summary.Period.Key), [t[PackageLabels.ColPeriod], t[PackageLabels.SumGeneratedOn]]);
        head.Add($"{AccountantPackageDocuments.Day(summary.Period.From)} - {AccountantPackageDocuments.Day(summary.Period.To)}", ReportCell.Date(summary.GeneratedOn));
        tables.Add(head);

        var stays = new ReportTable(
            t[PackageLabels.SumStaysTitle],
            [t[PackageLabels.SumOccupiedNights], t[PackageLabels.SumAvailableNights], t[PackageLabels.SumOccupancy], t[PackageLabels.SumRevenue], t[PackageLabels.SumStays], t[PackageLabels.SumAdr], t[PackageLabels.SumRevPar]]);
        var s = summary.Stays;
        stays.Add(s.OccupiedNights, s.AvailableNights, ReportCell.Percent(s.OccupancyRate), ReportCell.Money(s.Revenue), s.Stays, ReportCell.Money(s.Adr), ReportCell.Money(s.RevPar));
        tables.Add(stays);

        var collected = new ReportTable(t[PackageLabels.SumCollectionsTitle], [t[PackageLabels.SumCollected], t[PackageLabels.SumPayments]]);
        collected.Add(ReportCell.Money(summary.Collections.Collected), summary.Collections.Payments);
        tables.Add(collected);

        var balances = new ReportTable(t[PackageLabels.SumBalancesTitle], [currency, t[PackageLabels.SumToCollect], t[PackageLabels.SumStays]]);
        foreach (var b in summary.Collections.ToCollect)
            balances.Add(b.Currency, ReportCell.Money(b.Amount), b.StayCount);
        tables.Add(balances);

        var guest = new ReportTable(
            t[PackageLabels.SumGuestDocsTitle],
            [t[PackageLabels.ColIssuer], t["FiscalExportColKind"], currency, t[PackageLabels.ColDocuments], t["FiscalExportColTaxableBase"], t["FiscalExportColVat"], t["FiscalExportColTotal"]]);
        foreach (var r in summary.GuestDocuments)
        {
            guest.Add(
                AccountantPackageDocuments.IssuerLabel(r.Issuer),
                AccountantPackageDocuments.KindLabel(r.Kind, t),
                r.Currency,
                r.Documents,
                ReportCell.Money(r.Amounts.Taxable),
                ReportCell.Money(r.Amounts.Vat),
                ReportCell.Money(r.Amounts.Total));
        }

        tables.Add(guest);

        var costs = new ReportTable(
            t[PackageLabels.SumPortalCostsTitle],
            [t[PackageLabels.ColChannel], t[PackageLabels.SumCostKind], t[PackageLabels.SumStays], t[PackageLabels.SumGross], t[PackageLabels.SumCost], t[PackageLabels.SumNet], t[PackageLabels.SumCostRate]]);
        foreach (var c in summary.PortalCosts)
        {
            costs.Add(
                c.Source is { } source ? FiscalReportDocuments.ChannelLabel(source.ToString()) : t[PackageLabels.SumTotalRow],
                ChannelCostsReportDocuments.KindLabel(c.Kind),
                c.Stays,
                ReportCell.Money(c.Gross),
                ReportCell.Money(c.Cost),
                ReportCell.Money(c.Net),
                ReportCell.Percent(c.CostRate));
        }

        tables.Add(costs);
        tables.Add(TouristTax(summary, t));

        var commissions = new ReportTable(
            t[PackageLabels.SumCommissionsTitle],
            [
                t[PackageLabels.ColOwner],
                currency,
                t[PackageLabels.ColDocuments],
                t[PackageLabels.SumCommissionTaxable],
                t[PackageLabels.SumCommissionVat],
                t[PackageLabels.SumCommissionTotal],
                t[PackageLabels.SumRechargeTaxable],
                t[PackageLabels.SumRechargeVat],
                t[PackageLabels.SumRechargeTotal],
                t[PackageLabels.SumAdvanced],
                t[PackageLabels.SumInvoiced],
            ]);
        foreach (var r in summary.Commissions)
        {
            commissions.Add(
                AccountantPackageDocuments.OwnerLabel(r.OwnerName, t),
                r.Currency,
                r.Documents,
                ReportCell.Money(r.Commission.Taxable),
                ReportCell.Money(r.Commission.Vat),
                ReportCell.Money(r.Commission.Total),
                ReportCell.Money(r.Recharges.Taxable),
                ReportCell.Money(r.Recharges.Vat),
                ReportCell.Money(r.Recharges.Total),
                ReportCell.Money(r.AdvancedExpenses),
                ReportCell.Money(r.InvoicedTotal));
        }

        tables.Add(commissions);

        var counts = new ReportTable(t[PackageLabels.SumOwnerInvoicesTitle], [currency, t[PackageLabels.SumInvoices], t[PackageLabels.SumCreditNotes]]);
        foreach (var c in summary.OwnerInvoices.Counts)
            counts.Add(c.Currency, c.Invoices, c.CreditNotes);
        tables.Add(counts);

        var rates = new ReportTable(
            t[PackageLabels.SumOwnerInvoicesRatesTitle],
            [currency, t[PackageLabels.ColRate], t[PackageLabels.ColNature], t["FiscalExportColTaxableBase"], t["FiscalExportColVat"], t["FiscalExportColTotal"]]);
        foreach (var r in summary.OwnerInvoices.Rates)
        {
            rates.Add(
                r.Currency,
                AccountantPackageDocuments.RateCell(r.RatePercent),
                r.RatePercent is null ? t[PackageLabels.RateUnknown] : r.NatureCode,
                ReportCell.Money(r.Amounts.Taxable),
                ReportCell.Money(r.Amounts.Vat),
                ReportCell.Money(r.Amounts.Total));
        }

        tables.Add(rates);

        var payouts = new ReportTable(
            t[PackageLabels.SumPayoutsTitle],
            [currency, t[PackageLabels.SumPayouts], t[PackageLabels.SumGross], t[PackageLabels.SumWithholding], t[PackageLabels.SumNetPaid]]);
        foreach (var p in summary.Payouts)
            payouts.Add(p.Currency, p.Payouts, ReportCell.Money(p.Gross), ReportCell.Money(p.Withholding), ReportCell.Money(p.Net));
        tables.Add(payouts);

        var wires = new ReportTable(
            t[PackageLabels.SumWiresTitle],
            [currency, t["FiscalExportColPaymentState"], t[PackageLabels.SumBatches], t[PackageLabels.SumTransfers], t["FiscalExportColTotal"]]);
        foreach (var w in summary.Wires)
            wires.Add(w.Currency, WireLabel(w.Status, t), w.Batches, w.Transfers, ReportCell.Money(w.Total));
        tables.Add(wires);

        var borne = new ReportTable(t[PackageLabels.SumPortalBorneTitle], [t[PackageLabels.ColChannel], currency, t[PackageLabels.ColAmount]]);
        foreach (var b in summary.PortalCommissionsBorne)
            borne.Add(FiscalReportDocuments.ChannelLabel(b.Source.ToString()), b.Currency, ReportCell.Money(b.Amount));
        tables.Add(borne);

        var margin = new ReportTable(
            t[PackageLabels.SumMarginTitle],
            [currency, t[PackageLabels.SumCommissionTaxable], t[PackageLabels.SumRechargeTaxable], t[PackageLabels.SumPortalBorne], t[PackageLabels.SumMargin]]);
        foreach (var m in summary.Margin)
            margin.Add(m.Currency, ReportCell.Money(m.Commissions), ReportCell.Money(m.Recharges), ReportCell.Money(m.PortalCommissionsBorne), ReportCell.Money(m.Margin));
        tables.Add(margin);

        var owners = new ReportTable(
            t[PackageLabels.SumOwnersTitle],
            [t[PackageLabels.ColOwner], t[PackageLabels.SumProperties], t[PackageLabels.SumRevenue], t[PackageLabels.SumOccupiedNights], t[PackageLabels.SumAvailableNights], t[PackageLabels.SumOccupancy], t[PackageLabels.SumStays]]);
        foreach (var o in summary.Owners)
        {
            owners.Add(
                o.OwnerId is null ? t[PackageLabels.SumOwnersUnassigned] : AccountantPackageDocuments.OwnerLabel(o.OwnerName, t),
                o.Properties,
                ReportCell.Money(o.Figures.Revenue),
                ReportCell.Number(o.Figures.OccupiedNights),
                ReportCell.Number(o.Figures.AvailableNights),
                ReportCell.Percent(o.Figures.OccupancyRate),
                ReportCell.Number(o.Figures.StayCount));
        }

        tables.Add(owners);

        if (!summary.OwnerAccountingIncluded)
        {
            var omitted = new ReportTable(t[PackageLabels.SumOwnerAccountingOmitted], [t[PackageLabels.IndexColReason]]);
            omitted.Add(t[PackageLabels.OmittedOwnerAccounting]);
            tables.Add(omitted);
        }

        return tables;
    }

    /// <summary>The summary as a CSV: the tables one under the other, an empty row between them.</summary>
    public static byte[] Csv(AgencySummary summary, PackageTexts t) => CsvWriter.Write(Tables(summary, t));

    /// <summary>The summary as a workbook: a sheet for each table, with the same title row, header and rows as the CSV.</summary>
    public static byte[] Xlsx(AgencySummary summary, PackageTexts t) =>
        XlsxWriter.Write([.. Tables(summary, t).Select((table, index) => new ReportSheet(string.Create(CultureInfo.InvariantCulture, $"{index + 1:00} {SheetNameOf(table.Title)}"), table))]);

    /// <summary>The summary as a PDF: its title, then each table under its own heading.</summary>
    public static PdfDocumentContent Pdf(AgencySummary summary, PackageTexts t)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(t);

        var blocks = new List<PdfBlock>();
        foreach (var table in Tables(summary, t))
        {
            blocks.Add(new PdfHeading(table.Title, 3));
            blocks.Add(PdfTableOf(table, t.Language));
        }

        return new PdfDocumentContent(t.Get(PackageLabels.SummaryTitle, summary.Period.Key), blocks)
        {
            Language = DocumentLanguages.Normalize(t.Language) == DocumentLanguages.English ? "en-GB" : "it-IT",
        };
    }

    /// <summary>A table as the block of a PDF: the header, the rows as text in the number format of the language, the numbers to the right.</summary>
    internal static PdfTable PdfTableOf(ReportTable table, string language)
    {
        var culture = DocumentLanguages.CultureOf(language);
        var columns = table.Header.Count;
        var columnsOut = new List<PdfTableColumn>(columns);
        for (var c = 0; c < columns; c++)
        {
            var numeric = table.Rows.Any(row => c < row.Count && row[c].Kind is ReportCellKind.Integer or ReportCellKind.Decimal or ReportCellKind.Percent);
            columnsOut.Add(new PdfTableColumn(table.Header[c], 1, numeric ? PdfCellAlignment.Right : PdfCellAlignment.Left));
        }

        var rows = table.Rows
            .Select(row => (IReadOnlyList<string>)[.. Enumerable.Range(0, columns).Select(c => c < row.Count ? TextOf(row[c], culture) : string.Empty)])
            .ToList();
        return new PdfTable(columnsOut, rows);
    }

    private static string TextOf(ReportCell cell, CultureInfo culture) => cell.Kind switch
    {
        ReportCellKind.Text => cell.TextValue ?? string.Empty,
        ReportCellKind.Integer => cell.WholeValue.ToString("N0", culture),
        ReportCellKind.Decimal => cell.DecimalValue.ToString("N2", culture),
        ReportCellKind.Percent => (cell.DecimalValue * 100m).ToString("N2", culture) + "%",
        ReportCellKind.Date => cell.DayValue.ToString(ReportCell.CsvDateFormat, CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static ReportTable TouristTax(AgencySummary summary, PackageTexts t)
    {
        var table = new ReportTable(
            t[PackageLabels.SumTouristTaxTitle],
            [t[PackageLabels.SumArea], t["FiscalExportColCurrency"], t[PackageLabels.SumTaxCollected], t[PackageLabels.SumTaxRemitted], t[PackageLabels.SumTaxToRemit], t[PackageLabels.SumTaxIncomplete]]);
        var italy = summary.TouristTax.Italy;
        table.Add(
            t[PackageLabels.SumAreaItaly],
            StayCurrency.Default,
            ReportCell.Money(italy.Owed),
            ReportCell.Money(italy.Paid),
            ReportCell.Money(italy.ToRemit),
            italy.StaysWithoutAmount);
        foreach (var r in summary.TouristTax.Switzerland)
        {
            table.Add(
                t.Get(PackageLabels.SumAreaSwitzerland, string.IsNullOrEmpty(r.Canton) ? t[PackageLabels.CantonUnknown] : r.Canton),
                r.Currency,
                ReportCell.Money(r.Collected),
                ReportCell.Empty,
                ReportCell.Empty,
                ReportCell.Empty);
        }

        return table;
    }

    private static string WireLabel(WireBatchStatus status, PackageTexts t) => status switch
    {
        WireBatchStatus.Downloaded => t[PackageLabels.WireDownloaded],
        WireBatchStatus.Paid => t[PackageLabels.WirePaid],
        _ => t[PackageLabels.WireGenerated],
    };

    /// <summary>The words that name a sheet: the title up to its first comma or colon, the numbers in front of it keep the order of the tables.</summary>
    private static string SheetNameOf(string title)
    {
        var cut = title.IndexOfAny([',', ':', ';', '(']);
        return (cut > 0 ? title[..cut] : title).Trim();
    }
}
