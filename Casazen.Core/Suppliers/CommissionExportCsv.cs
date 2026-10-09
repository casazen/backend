using System.Globalization;
using System.Text;
using Casazen.Core.Services;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The monthly commission export as CSV (SP-15b, decision D4): what the accountant needs to invoice CasaZen's commission by hand
/// while the VAT treatment and DAC7 are open (<b>[CONSULENTE FISCALE]</b>). One line per payment collected through Stripe in the
/// month and one per refund that succeeded in it (negative amounts), with the supplier's P.IVA and the gross amount, so the same
/// file feeds a future DAC7 report. RFC 4180 (comma, <c>"</c> doubling, CRLF); money in integer cents; dates are ISO days in Rome.
/// </summary>
/// <remarks>
/// <para><b>Spreadsheet formula injection.</b> A supplier chooses its own name: a text that starts with <c>=</c>, <c>+</c>, <c>-</c>,
/// <c>@</c>, a tab or a carriage return would run as a formula when the file is opened in a spreadsheet, so the text columns get
/// a leading apostrophe in that case. The numeric columns are written as numbers and are never touched (a refund is negative).</para>
/// <para><b>VAT.</b> The <c>vat_percent</c> column repeats the configured rate (<c>SupplierPayments:CommissionVatPercent</c>) and is
/// empty while the consultant has not decided; no VAT amount is computed, because whether the percentage includes VAT or VAT is
/// added to it is exactly what is open.</para>
/// </remarks>
public static class CommissionExportCsv
{
    /// <summary>The content type of the file.</summary>
    public const string ContentType = "text/csv";

    /// <summary>The columns, in order.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "type", "date", "payment_id", "service_request_id", "supplier_org_id", "supplier_name", "supplier_vat_number",
        "payer_org_id", "payer_name", "currency", "gross_cents", "commission_percent", "commission_cents", "vat_percent", "net_cents",
    ];

    /// <summary>The file name for a month: <c>commissioni-2026-10.csv</c>.</summary>
    public static string FileName(int year, int month) => $"commissioni-{year:D4}-{month:D2}.csv";

    /// <summary>The CSV text of the export: the header, then one line per row, each ended by CRLF.</summary>
    public static string Write(CommissionExport export)
    {
        ArgumentNullException.ThrowIfNull(export);

        var csv = new StringBuilder();
        csv.Append(string.Join(',', Columns)).Append("\r\n");

        var vat = export.VatPercent is { } rate ? Number(rate) : string.Empty;
        foreach (var row in export.Rows)
        {
            csv.Append(string.Join(',', new[]
            {
                Text(row.Type),
                row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.PaymentId.ToString("D"),
                row.ServiceRequestId.ToString("D"),
                row.SupplierOrgId.ToString("D"),
                Text(row.SupplierName),
                Text(row.SupplierVatNumber),
                row.PayerOrgId?.ToString("D") ?? string.Empty,
                Text(row.PayerName),
                Text(row.Currency.ToUpperInvariant()),
                row.GrossCents.ToString(CultureInfo.InvariantCulture),
                Number(row.CommissionPercent),
                row.CommissionCents.ToString(CultureInfo.InvariantCulture),
                vat,
                row.NetCents.ToString(CultureInfo.InvariantCulture),
            })).Append("\r\n");
        }

        return csv.ToString();
    }

    /// <summary>A percentage with up to two decimals, with a dot, whatever the culture of the server.</summary>
    private static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A text field: neutralized when a spreadsheet would read it as a formula, then quoted when it needs to be.</summary>
    private static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
