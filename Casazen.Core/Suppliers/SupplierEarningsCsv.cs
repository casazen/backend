using System.Globalization;
using System.Text;
using Casazen.Core.Utilities;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The supplier's own earnings as CSV for its accountant (SP-16a): the rows of the list, with the same filters, in the same style as the staff export
/// of the commission (<see cref="CommissionExportCsv"/>: RFC 4180, comma, <c>"</c> doubling, CRLF, money in integer cents, dates as ISO days in Rome).
/// </summary>
/// <remarks>
/// <para><b>No contact.</b> A customer is named by the same label the console shows for a taken job (decision D9); no e-mail address and no phone, and
/// an anonymized customer is an empty cell (decision SP-D25: the supplier's customers' contacts are never exported).</para>
/// <para><b>Spreadsheet formula injection.</b> The service and the customer are names somebody else chose: a text that starts with <c>=</c>, <c>+</c>,
/// <c>-</c>, <c>@</c>, a tab or a carriage return gets a leading apostrophe (<see cref="CommissionExportCsv"/> does it). The numeric columns are numbers
/// and are never touched. No VAT is computed: the file adds nothing to what the payments record (decision D4, <b>[CONSULENTE FISCALE]</b>).</para>
/// <para>An amount that does not exist yet (a job with no declared total, a commission not computed before the payment) is an empty cell, never 0.</para>
/// </remarks>
public static class SupplierEarningsCsv
{
    /// <summary>The content type of the file.</summary>
    public const string ContentType = "text/csv";

    /// <summary>The columns, in order.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "completed_on", "paid_on", "service_request_id", "service", "client", "state", "payment_mode", "paid_via", "currency",
        "gross_cents", "commission_percent", "commission_cents", "net_cents", "refunded_cents",
    ];

    /// <summary>The file name: <c>incassi-2026-10-17.csv</c>, the day of the export in Rome.</summary>
    public static string FileName(DateOnly today) => $"incassi-{today.Year:D4}-{today.Month:D2}-{today.Day:D2}.csv";

    /// <summary>The CSV text of the export: the header, then one line per row, each ended by CRLF.</summary>
    public static string Write(IReadOnlyList<SupplierEarningRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var csv = new StringBuilder();
        csv.Append(string.Join(',', Columns)).Append("\r\n");
        foreach (var row in rows)
        {
            csv.Append(string.Join(',', new[]
            {
                Day(row.CompletedAt),
                Day(row.PaidAt),
                row.RequestId.ToString("D"),
                CommissionExportCsv.Text(row.ServiceName),
                CommissionExportCsv.Text(row.ClientLabel),
                row.State,
                row.PaymentMode.ToString(),
                row.PaidVia?.ToString() ?? string.Empty,
                "EUR",
                Cents(row.GrossCents),
                row.CommissionPercent is { } percent ? CommissionExportCsv.Number(percent) : string.Empty,
                Cents(row.CommissionCents),
                Cents(row.NetCents),
                row.RefundedCents.ToString(CultureInfo.InvariantCulture),
            })).Append("\r\n");
        }

        return csv.ToString();
    }

    private static string Day(DateTime? utc) =>
        utc is { } value ? RomeCalendar.DateInRome(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

    private static string Cents(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
