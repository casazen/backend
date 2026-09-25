using System.Globalization;
using System.Text;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// PDF content and CSV of the fiscal reports (CO-19, A5-23), in Italian. The PDF is laid out by
/// <see cref="IPdfDocumentRenderer"/> (A4, tables with the header repeated on every page); legal references come from
/// configuration (<see cref="FiscalReportRules"/>), never written here, because they change number in 2027 (fiscale.md).
/// The CSV is for spreadsheets set to Italian: UTF-8 with BOM, <c>;</c> separator, decimal comma, one table per file.
/// Column widths keep a 6-digit amount, a date and a booking code on one line at the renderer's table font.
/// </summary>
public static class FiscalReportDocuments
{
    private const string NotAvailable = "n.d.";
    private const char CsvSeparator = ';';

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    // ---------------------------------------------------------------- summary

    public static PdfDocumentContent Pdf(AnnualIncomeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var blocks = new List<PdfBlock>();
        AddHeader(blocks, report.PackLabel, report.OrgName, report.Period, report.GeneratedOn, report.TaxYear);

        blocks.Add(new PdfHeading("Incassi per immobile"));
        if (report.Properties.Count == 0)
        {
            blocks.Add(new PdfParagraph("Nessun immobile con affitti brevi o incassi nel periodo."));
        }
        else
        {
            var incomeRows = report.Properties
                .Select(l => Row(l.Name, RegimeLabel(l.Regime, report.Rules), Money(l.GrossIncome), Money(l.TouristTax), Money(l.RentalIncome), Money(l.Withholding)))
                .Append(Row("Totale", string.Empty, Money(report.Totals.GrossIncome), Money(report.Totals.TouristTax), Money(report.Totals.RentalIncome), Money(report.Totals.Withholding)))
                .ToList();
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn("Immobile", 1.6),
                    new PdfTableColumn("Regime", 1.6),
                    Amount("Incassato lordo (€)"),
                    Amount("Tassa di soggiorno (€)"),
                    Amount("Canone lordo (€)"),
                    Amount("Ritenute (€)"),
                ],
                incomeRows));

            blocks.Add(new PdfHeading("Imposta stimata per immobile"));
            var taxRows = report.Properties
                .Select(l => Row(
                    l.Name,
                    RegimeLabel(l.Regime, report.Rules),
                    l.TaxableIncome is decimal taxable ? Money(taxable) : "—",
                    l.TaxRate is decimal rate ? Percent(rate) : "—",
                    l.EstimatedTax is decimal tax ? Money(tax) : "—",
                    l.TaxNote is null ? string.Empty : TaxNoteText(l.TaxNote)))
                .Append(Row("Totale", string.Empty, Money(report.Totals.TaxableIncome), string.Empty, Money(report.Totals.EstimatedTax), string.Empty))
                .ToList();
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn("Immobile", 1.5),
                    new PdfTableColumn("Regime", 1.4),
                    Amount("Imponibile (€)", 1.1),
                    new PdfTableColumn("Aliquota", 0.9, PdfCellAlignment.Right),
                    Amount("Imposta stimata (€)"),
                    new PdfTableColumn("Nota", 1.5),
                ],
                taxRows));
        }

        if (report.Taxpayers.Count > 0)
        {
            blocks.Add(new PdfHeading("Totali per titolare"));
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn("Titolare", 2.4),
                    new PdfTableColumn("Immobili", 0.9, PdfCellAlignment.Right),
                    Amount("Canone lordo (€)"),
                    Amount("Ritenute (€)"),
                    Amount("Imposta stimata (€)"),
                ],
                report.Taxpayers
                    .Select(t => Row(
                        TaxpayerLabel(t),
                        t.Properties.ToString(Italian),
                        Money(t.RentalIncome),
                        Money(t.Withholding),
                        Money(t.EstimatedTax)))
                    .ToList()));
        }

        var rules = report.Rules;
        blocks.Add(new PdfHeading("Note e fonti"));
        blocks.Add(new PdfParagraph(
            "Incassato lordo: pagamenti completati nel periodo (data di incasso nel fuso orario Europe/Rome), al netto dei "
            + "rimborsi. Comprende la tassa di soggiorno che l'ospite ha pagato insieme al soggiorno."));
        blocks.Add(new PdfParagraph(
            "Tassa di soggiorno: importo registrato sulla prenotazione dal calcolo di CasaZen, ripartito tra i pagamenti "
            + "della prenotazione in proporzione all'importo. È un'imposta del comune a carico dell'ospite, riscossa per "
            + "conto del comune: è esclusa dal canone lordo."));
        blocks.Add(new PdfParagraph(
            $"Commissioni: {NotAvailable} CasaZen non registra le commissioni degli intermediari né i costi di incasso: "
            + "riportale dagli estratti conto. Le spese sono deducibili solo in regime d'impresa o per il sublocatore, "
            + "quindi non riducono l'imponibile della cedolare secca."));
        blocks.Add(new PdfParagraph(WithholdingNote(rules)));
        blocks.Add(new PdfParagraph(
            $"Imposta stimata: solo per la cedolare secca, sul canone lordo senza deduzioni, con l'aliquota del "
            + $"{Percent(rules.CedolareRate)} o del {Percent(rules.CedolareReducedRate)} per l'unica unità designata dal "
            + $"titolare per l'anno ({rules.CedolareSource}). Le ritenute subite si scomputano in dichiarazione."));
        blocks.Add(new PdfParagraph(
            "Non calcolata per: IRPEF ordinaria senza partita IVA (dipende dagli altri redditi del contribuente e dalla "
            + "rendita catastale), regimi d'impresa ordinario e forfettario (dipendono da costi e coefficiente di "
            + "redditività), immobili senza regime assegnato e titolari che destinano alla locazione breve più di "
            + $"{rules.MaxApartmentsPerTaxpayer} appartamenti nell'anno ({rules.ThresholdSource}), per i quali si presume "
            + "l'attività d'impresa."));
        blocks.Add(new PdfParagraph(report.Disclaimer));

        return new PdfDocumentContent($"Riepilogo fiscale affitti brevi {report.TaxYear}", blocks);
    }

    public static byte[] Csv(AnnualIncomeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var taxpayers = report.Taxpayers.ToDictionary(t => t.Index);
        var rows = new List<IReadOnlyList<string>>
        {
            Row(
                "Immobile",
                "Regime",
                "Titolare",
                "Incassato lordo",
                "Tassa di soggiorno",
                "Canone lordo",
                "Commissioni",
                "Ritenute",
                "Netto incassato",
                "Imponibile",
                "Aliquota",
                "Imposta stimata",
                "Nota"),
        };
        foreach (var l in report.Properties)
        {
            rows.Add(Row(
                l.Name,
                RegimeLabel(l.Regime, report.Rules),
                l.TaxpayerIndex is int index && taxpayers.TryGetValue(index, out var taxpayer) ? TaxpayerLabel(taxpayer) : string.Empty,
                CsvAmount(l.GrossIncome),
                CsvAmount(l.TouristTax),
                CsvAmount(l.RentalIncome),
                l.Commissions is decimal commissions ? CsvAmount(commissions) : NotAvailable,
                CsvAmount(l.Withholding),
                CsvAmount(l.Net),
                l.TaxableIncome is decimal taxable ? CsvAmount(taxable) : string.Empty,
                l.TaxRate is decimal rate ? Percent(rate) : string.Empty,
                l.EstimatedTax is decimal tax ? CsvAmount(tax) : string.Empty,
                l.TaxNote is null ? string.Empty : TaxNoteText(l.TaxNote)));
        }

        var totals = report.Totals;
        rows.Add(Row(
            "Totale",
            string.Empty,
            string.Empty,
            CsvAmount(totals.GrossIncome),
            CsvAmount(totals.TouristTax),
            CsvAmount(totals.RentalIncome),
            NotAvailable,
            CsvAmount(totals.Withholding),
            CsvAmount(totals.Net),
            CsvAmount(totals.TaxableIncome),
            string.Empty,
            CsvAmount(totals.EstimatedTax),
            string.Empty));
        return WriteCsv(rows);
    }

    // ---------------------------------------------------------------- withholding

    public static PdfDocumentContent Pdf(WithholdingReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var blocks = new List<PdfBlock>();
        AddHeader(blocks, report.PackLabel, report.OrgName, report.Period, report.GeneratedOn, report.TaxYear);

        blocks.Add(new PdfHeading("Ritenute per intermediario"));
        if (report.Lines.Count == 0)
        {
            blocks.Add(new PdfParagraph("Nessuna ritenuta registrata nel periodo."));
        }
        else
        {
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn("Canale", 1.6),
                    new PdfTableColumn("Pagamenti", 1, PdfCellAlignment.Right),
                    Amount("Lordo (€)"),
                    Amount("Ritenuta (€)"),
                    Amount("Netto (€)"),
                ],
                report.ByOta
                    .Select(b => Row(ChannelLabel(b.Source), b.PayoutCount.ToString(Italian), Money(b.Gross), Money(b.Withholding), Money(b.Net)))
                    .Append(Row("Totale", report.Totals.PayoutCount.ToString(Italian), Money(report.Totals.Gross), Money(report.Totals.Withholding), Money(report.Totals.Net)))
                    .ToList()));

            blocks.Add(new PdfHeading("Dettaglio dei pagamenti"));
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn("Data", 1.2),
                    new PdfTableColumn("Immobile", 1.6),
                    new PdfTableColumn("Prenotazione", 1.6),
                    new PdfTableColumn("Canale", 1.3),
                    Amount("Lordo (€)", 1.1),
                    Amount("Ritenuta (€)", 1.1),
                ],
                report.Lines
                    .Select(l => Row(Date(l.PaidOn), l.PropertyName, l.BookingCode, ChannelLabel(l.Source), Money(l.Gross), Money(l.Withholding)))
                    .ToList()));
        }

        blocks.Add(new PdfHeading("Note e fonti"));
        blocks.Add(new PdfParagraph(WithholdingNote(report.Rules)));
        blocks.Add(new PdfParagraph(
            "Lordo: importo del pagamento al netto dei rimborsi; la ritenuta è ridotta in proporzione ai rimborsi. Netto: "
            + "lordo meno ritenuta. Sono inclusi i pagamenti completati nel periodo (data di incasso nel fuso orario "
            + "Europe/Rome) con una ritenuta registrata, calcolata da CasaZen con l'aliquota configurata o indicata dall'host."));
        blocks.Add(new PdfParagraph(report.Disclaimer));

        return new PdfDocumentContent($"Ritenute subite dagli intermediari {report.TaxYear}", blocks);
    }

    public static byte[] Csv(WithholdingReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var rows = new List<IReadOnlyList<string>>
        {
            Row("Data", "Immobile", "Prenotazione", "Canale", "Lordo", "Ritenuta", "Netto", "Origine della ritenuta"),
        };
        rows.AddRange(report.Lines.Select(l => Row(
            IsoDate(l.PaidOn),
            l.PropertyName,
            l.BookingCode,
            ChannelLabel(l.Source),
            CsvAmount(l.Gross),
            CsvAmount(l.Withholding),
            CsvAmount(l.Net),
            l.WithholdingSource switch
            {
                WithholdingSource.AutoOta => "Calcolata da CasaZen",
                WithholdingSource.Manual => "Indicata dall'host",
                _ => string.Empty,
            })));
        rows.Add(Row(
            "Totale",
            string.Empty,
            string.Empty,
            string.Empty,
            CsvAmount(report.Totals.Gross),
            CsvAmount(report.Totals.Withholding),
            CsvAmount(report.Totals.Net),
            string.Empty));
        return WriteCsv(rows);
    }

    // ---------------------------------------------------------------- tourist tax

    public static PdfDocumentContent Pdf(TouristTaxReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var blocks = new List<PdfBlock>
        {
            new PdfParagraph($"Organizzazione: {report.OrgName}"),
            new PdfParagraph($"Periodo: dal {Date(report.Period.From)} al {Date(report.Period.To)} (soggiorni con check-in nel periodo)"),
            new PdfParagraph($"Generato il {Date(report.GeneratedOn)}. Importi in euro."),
        };

        blocks.Add(new PdfHeading("Per comune e mese"));
        if (report.Rows.Count == 0)
        {
            blocks.Add(new PdfParagraph("Nessun soggiorno con check-in nel periodo."));
        }
        else
        {
            blocks.Add(new PdfTable(
                TouristTaxColumns("Mese"),
                report.Rows
                    .Select(r => Row(r.Comune, MonthLabel(r.Year, r.Month), Count(r.Stays), Count(r.Nights), Count(r.Guests), Money(r.Amount), Count(r.StaysWithoutAmount)))
                    .Append(Row("Totale", string.Empty, Count(report.Totals.Stays), Count(report.Totals.Nights), Count(report.Totals.Guests), Money(report.Totals.Amount), Count(report.Totals.StaysWithoutAmount)))
                    .ToList()));

            blocks.Add(new PdfHeading("Totali per comune"));
            blocks.Add(new PdfTable(
                TouristTaxColumns(null),
                report.ByComune
                    .Select(c => Row(c.Comune, Count(c.Stays), Count(c.Nights), Count(c.Guests), Money(c.Amount), Count(c.StaysWithoutAmount)))
                    .ToList()));

            foreach (var comune in report.Stays.GroupBy(s => s.Comune))
            {
                blocks.Add(new PdfHeading($"Soggiorni a {comune.Key}", 3));
                blocks.Add(new PdfTable(
                    [
                        new PdfTableColumn("Prenotazione", 1.6),
                        new PdfTableColumn("Immobile", 1.8),
                        new PdfTableColumn("Check-in", 1.2),
                        new PdfTableColumn("Check-out", 1.2),
                        new PdfTableColumn("Notti", 0.7, PdfCellAlignment.Right),
                        new PdfTableColumn("Ospiti", 0.8, PdfCellAlignment.Right),
                        Amount("Imposta (€)", 1.1),
                    ],
                    comune
                        .Select(s => Row(s.BookingCode, s.PropertyName, Date(s.CheckIn), Date(s.CheckOut), Count(s.Nights), Count(s.Guests), s.Amount > 0 ? Money(s.Amount) : "—"))
                        .ToList()));
            }
        }

        blocks.Add(new PdfHeading("Note"));
        blocks.Add(new PdfParagraph(
            "Soggiorni confermati, in corso o conclusi con check-in nel periodo; le prenotazioni annullate o in attesa sono "
            + "escluse. Il comune è quello indicato nell'indirizzo dell'immobile."));
        blocks.Add(new PdfParagraph(
            "Imposta registrata: importo calcolato da CasaZen al momento della prenotazione con le tariffe del comune "
            + "(notti tassabili, esenzioni e riduzioni per età) e incluso nel totale pagato dall'ospite. Il report non lo "
            + "ricalcola."));
        blocks.Add(new PdfParagraph(
            "Senza importo: soggiorni senza imposta registrata (tariffa del comune non disponibile in CasaZen, ospiti "
            + "esenti, prenotazioni inserite da un intermediario o prima del calcolo). Verificali prima del versamento."));
        blocks.Add(new PdfParagraph(
            "Notti e ospiti sono quelli della prenotazione: il tetto di notti e le esenzioni del regolamento comunale "
            + "possono ridurre i pernottamenti tassabili."));
        blocks.Add(new PdfParagraph(report.Disclaimer));

        return new PdfDocumentContent("Tassa di soggiorno per comune e periodo", blocks);
    }

    public static byte[] Csv(TouristTaxReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var rows = new List<IReadOnlyList<string>>
        {
            Row("Comune", "Anno", "Mese", "Soggiorni", "Notti", "Ospiti", "Imposta registrata", "Soggiorni senza importo"),
        };
        rows.AddRange(report.Rows.Select(r => Row(
            r.Comune,
            r.Year.ToString(CultureInfo.InvariantCulture),
            r.Month.ToString(CultureInfo.InvariantCulture),
            r.Stays.ToString(CultureInfo.InvariantCulture),
            r.Nights.ToString(CultureInfo.InvariantCulture),
            r.Guests.ToString(CultureInfo.InvariantCulture),
            CsvAmount(r.Amount),
            r.StaysWithoutAmount.ToString(CultureInfo.InvariantCulture))));
        var t = report.Totals;
        rows.Add(Row(
            "Totale",
            string.Empty,
            string.Empty,
            t.Stays.ToString(CultureInfo.InvariantCulture),
            t.Nights.ToString(CultureInfo.InvariantCulture),
            t.Guests.ToString(CultureInfo.InvariantCulture),
            CsvAmount(t.Amount),
            t.StaysWithoutAmount.ToString(CultureInfo.InvariantCulture)));
        return WriteCsv(rows);
    }

    // ---------------------------------------------------------------- shared

    /// <summary>Italian label of a regime, with the rate from configuration for the cedolare.</summary>
    public static string RegimeLabel(StrFiscalRegime? regime, FiscalReportRules rules) => regime switch
    {
        StrFiscalRegime.CedolareSecca21 => $"Cedolare secca {Percent(rules.CedolareReducedRate)} (unità designata)",
        StrFiscalRegime.CedolareSecca26 => $"Cedolare secca {Percent(rules.CedolareRate)}",
        StrFiscalRegime.IrpefOrdinaria => "IRPEF ordinaria senza partita IVA",
        StrFiscalRegime.RegimeOrdinario => "Impresa, regime ordinario",
        StrFiscalRegime.RegimeForfettario => "Impresa, regime forfettario",
        _ => "Non assegnato",
    };

    /// <summary>Italian text of a <see cref="FiscalTaxNotes"/> code.</summary>
    public static string TaxNoteText(string note) => note switch
    {
        FiscalTaxNotes.IrpefOrdinariaNotComputed => "Non calcolata: IRPEF ordinaria, a cura del contribuente o del commercialista",
        FiscalTaxNotes.ImpresaNotComputed => "Non calcolata: regime d'impresa, a cura del commercialista",
        FiscalTaxNotes.RegimeNotAssigned => "Non calcolata: regime non assegnato",
        FiscalTaxNotes.ThresholdExceeded => "Non calcolata: titolare oltre la soglia, attività d'impresa presunta",
        _ => note,
    };

    private static void AddHeader(List<PdfBlock> blocks, string packLabel, string orgName, FiscalReportPeriod period, DateOnly generatedOn, int taxYear)
    {
        blocks.Add(new PdfParagraph(packLabel, Bold: true));
        blocks.Add(new PdfParagraph($"Organizzazione: {orgName}"));
        blocks.Add(new PdfParagraph($"Periodo: dal {Date(period.From)} al {Date(period.To)} (periodo d'imposta {taxYear})"));
        blocks.Add(new PdfParagraph($"Generato il {Date(generatedOn)}. Importi in euro."));
    }

    private static string WithholdingNote(FiscalReportRules rules) =>
        $"Ritenute: ritenuta del {Percent(rules.OtaWithholdingRate)} operata dagli intermediari che incassano i canoni delle "
        + $"locazioni brevi ({rules.OtaWithholdingSource}), sempre a titolo d'acconto: si scomputa in dichiarazione. Non si "
        + "applica ai contratti in regime d'impresa. Riconcilia gli importi con la Certificazione Unica rilasciata "
        + "dall'intermediario.";

    private static IReadOnlyList<PdfTableColumn> TouristTaxColumns(string? periodHeader)
    {
        var columns = new List<PdfTableColumn> { new("Comune", 1.6) };
        if (periodHeader is not null)
            columns.Add(new PdfTableColumn(periodHeader, 1.3));
        columns.AddRange(
        [
            new PdfTableColumn("Soggiorni", 1.1, PdfCellAlignment.Right),
            new PdfTableColumn("Notti", 0.8, PdfCellAlignment.Right),
            new PdfTableColumn("Ospiti", 0.8, PdfCellAlignment.Right),
            Amount("Imposta registrata (€)", 1.2),
            new PdfTableColumn("Senza importo", 1.1, PdfCellAlignment.Right),
        ]);
        return columns;
    }

    private static string TaxpayerLabel(AnnualTaxpayerTotals taxpayer)
    {
        var label = taxpayer.IsOrgTaxProfile
            ? "Profilo fiscale dell'organizzazione" + (taxpayer.FiscalCodeMasked is null ? string.Empty : $" (C.F. {taxpayer.FiscalCodeMasked})")
            : $"C.F. {taxpayer.FiscalCodeMasked}";
        return taxpayer.ThresholdExceeded ? $"{label}, oltre la soglia" : label;
    }

    /// <summary>Display name of a booking source (enum name for the unknown ones).</summary>
    private static string ChannelLabel(string source) => source switch
    {
        nameof(BookingSource.BookingCom) => "Booking.com",
        nameof(BookingSource.Direct) => "Sito di prenotazione",
        nameof(BookingSource.Manual) => "Inserita dall'host",
        nameof(BookingSource.Local) => "Locale",
        _ => source,
    };

    private static PdfTableColumn Amount(string header, double width = 1) => new(header, width, PdfCellAlignment.Right);

    private static IReadOnlyList<string> Row(params string[] cells) => cells;

    private static string Money(decimal value) => value.ToString("N2", Italian);

    private static string Count(int value) => value.ToString("N0", Italian);

    private static string Percent(decimal rate) => (rate * 100m).ToString("0.##", Italian) + "%";

    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string IsoDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string MonthLabel(int year, int month) => new DateOnly(year, month, 1).ToString("MMMM yyyy", Italian);

    /// <summary>Amount for a spreadsheet set to Italian: decimal comma, no thousands separator.</summary>
    private static string CsvAmount(decimal value) => value.ToString("0.00", Italian);

    private static byte[] WriteCsv(IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.Append(string.Join(CsvSeparator, row.Select(CsvCell))).Append("\r\n");
        var bom = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        return [.. bom, .. body];
    }

    /// <summary>
    /// A CSV cell: quoted when it holds the separator, a quote or a line break. Text starting with <c>= + - @</c> (a
    /// property name, for example) gets a leading apostrophe so a spreadsheet never runs it as a formula; amounts are
    /// never negative here.
    /// </summary>
    private static string CsvCell(string value)
    {
        var text = value;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            text = "'" + text;
        return text.IndexOfAny([CsvSeparator, '"', '\n', '\r']) >= 0
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }
}
