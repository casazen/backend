using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

/// <summary>
/// Where a comune stands in a period (NF-100, CHK-07): the tax the stays owe against the tax that was paid. Worked out on the
/// <b>recorded</b> amounts only; a stay with no recorded tax is never taken as a tax of zero, it is counted apart
/// (<see cref="ComuneTaxSummary.StaysWithoutAmount"/>), so a status never says more than the figures behind it.
/// </summary>
public enum TouristTaxSummaryStatus
{
    /// <summary>
    /// Nothing recorded on the stays and nothing paid. It does not say that no tax is due: when <see cref="ComuneTaxSummary.StaysWithoutAmount"/>
    /// is above zero the owed amount may be incomplete, and the page has to say so.
    /// </summary>
    NothingOwed,

    /// <summary>More is owed than was paid (the balance is above zero).</summary>
    ToPay,

    /// <summary>Owed and paid are the same amount, to the cent, and above zero.</summary>
    Paid,

    /// <summary>More was paid than is owed (the balance is below zero), including a payment for a comune with nothing recorded.</summary>
    Overpaid,
}

/// <summary>
/// One payment of a comune as the summary lists it (<see cref="TouristTaxRemittance"/>): the period it declares and pays, the amount, the day
/// it was paid, the reference and the note of the host, and whether a receipt is stored.
/// </summary>
/// <param name="Comune">The comune as the host typed it.</param>
/// <param name="PeriodFrom">First day of the period the payment pays (Europe/Rome calendar date).</param>
/// <param name="PeriodTo">Last day of that period, included.</param>
/// <param name="Amount">Euros, to the cent, above zero.</param>
/// <param name="PaidOn">The day the payment was made (Europe/Rome calendar date).</param>
/// <param name="HasReceipt">A receipt is stored and can be downloaded (<c>ITouristTaxSummaryService.OpenReceiptAsync</c>).</param>
public sealed record TouristTaxRemittanceLine(
    Guid Id,
    string Comune,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    decimal Amount,
    DateOnly PaidOn,
    string? Reference,
    string? Notes,
    bool HasReceipt);

/// <summary>
/// One comune in a period: its stays and the tax owed on them (the report of the fiscal area, not recomputed), the tax paid and the
/// balance.
/// </summary>
/// <param name="Comune">The comune as the tourist tax report names it (its most frequent spelling among the properties), or, for a comune that
/// only has payments in the period, as the host typed it.</param>
/// <param name="Stays">Confirmed stays with check-in in the period (the report's).</param>
/// <param name="Owed">The tax the engine recorded on those stays (BK-03), to the cent. The sum of the recorded amounts, nothing estimated.</param>
/// <param name="StaysWithoutAmount">Stays with no tax recorded (rate not available, exempt guests, a booking from a channel...). Counted apart: the
/// owed amount may be incomplete.</param>
/// <param name="Paid">The sum of the payments of the comune whose period lies inside the period asked; <c>null</c> when the summary does not show
/// the payments (<see cref="TouristTaxSummary.PaymentsHidden"/>).</param>
/// <param name="Balance"><see cref="Owed"/> minus <see cref="Paid"/>, negative when more was paid than is owed; <c>null</c> when the payments are hidden.</param>
/// <param name="Status">Where the comune stands; <c>null</c> when the payments are hidden, because the owed amount alone says nothing about it.</param>
/// <param name="Remittances">Those payments, oldest period first; <c>null</c> when the payments are hidden.</param>
public sealed record ComuneTaxSummary(
    string Comune,
    int Stays,
    int Nights,
    int Guests,
    decimal Owed,
    int StaysWithoutAmount,
    decimal? Paid,
    decimal? Balance,
    TouristTaxSummaryStatus? Status,
    IReadOnlyList<TouristTaxRemittanceLine>? Remittances);

/// <summary>
/// The sums of the comuni of a summary. <see cref="Balance"/> is the difference of the two totals: an overpayment to one comune is set
/// against what is owed to another in this one figure, so the rows are what says what is due to each comune. <see cref="Paid"/> and
/// <see cref="Balance"/> are <c>null</c> when the summary does not show the payments (<see cref="TouristTaxSummary.PaymentsHidden"/>).
/// </summary>
public sealed record TouristTaxSummaryTotals(
    int Stays,
    int Nights,
    int Guests,
    decimal Owed,
    int StaysWithoutAmount,
    decimal? Paid,
    decimal? Balance);

/// <summary>The tourist tax owed, paid and receipted per comune for a period (NF-100, REP-05).</summary>
/// <param name="Period">The days the summary covers.</param>
/// <param name="Comuni">The comuni with stays or, when the payments are shown, with payments in the period.</param>
/// <param name="Total">The sums of the comuni.</param>
/// <param name="PaymentsHidden">
/// True when the caller reaches only some of the properties of the org. A payment belongs to the org and to no property, so the summary then
/// shows what is owed on the properties reached and nothing about the payments: paid, balance, status and payments are <c>null</c>. A balance
/// of every payment of the org against the tax of some of its properties would be false.
/// </param>
public sealed record TouristTaxSummary(
    FiscalReportPeriod Period,
    IReadOnlyList<ComuneTaxSummary> Comuni,
    TouristTaxSummaryTotals Total,
    bool PaymentsHidden = false);

/// <summary>
/// What a payment is recorded with. The org is the caller's (the <see cref="Casazen.Core.Authorization.HostScope"/>), never part of the request.
/// </summary>
/// <param name="Comune">The comune as the host typed it: it has to be one of the comuni of the org's properties.</param>
/// <param name="PeriodFrom">First day of the period the payment pays.</param>
/// <param name="PeriodTo">Last day of that period, included; not before <paramref name="PeriodFrom"/>, at most a year later.</param>
/// <param name="PaidAmount">Euros, above zero, at most two decimals.</param>
/// <param name="PaidOn">The day it was paid: not after today in Europe/Rome.</param>
/// <param name="Reference">The reference the host wrote; blank means none.</param>
/// <param name="Notes">A note of the host; blank means none.</param>
/// <param name="RecordedByUserId">The user who records it (<c>User.Id</c>).</param>
/// <param name="ReceiptFileName">The name of the file the host sent as receipt, only to name the download; the stored name is made by the server.</param>
public sealed record RecordRemittanceRequest(
    string Comune,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    decimal PaidAmount,
    DateOnly PaidOn,
    string? Reference,
    string? Notes,
    string RecordedByUserId,
    string? ReceiptFileName = null);

/// <summary>The stored receipt of a payment; the caller disposes <see cref="Content"/>.</summary>
public sealed record TouristTaxReceiptFile(Stream Content, string FileName, string ContentType);

/// <summary>Input limits of the payments of the tourist tax.</summary>
public static class TouristTaxRemittanceLimits
{
    /// <summary>Largest receipt accepted: a PDF or a photo of a payment slip.</summary>
    public const long MaxReceiptBytes = 10 * 1024 * 1024;

    /// <summary>The largest amount of one payment, in euros: it fits the column with room, and no comune is paid more than this in one go.</summary>
    public const decimal MaxPaidAmount = 1_000_000_000m;
}

/// <summary>Error codes of the payments of the tourist tax (ProblemDetails <c>code</c>, FD-05).</summary>
public static class TouristTaxErrorCodes
{
    /// <summary>422: the comune is not one of the comuni of the properties the caller reaches.</summary>
    public const string ComuneUnknown = "tourist_tax_comune_unknown";

    /// <summary>422: the amount is not above zero, has more than two decimals or is too large.</summary>
    public const string AmountInvalid = "tourist_tax_amount_invalid";

    /// <summary>422: the day of the payment is after today in Europe/Rome.</summary>
    public const string DateInFuture = "tourist_tax_date_in_future";

    /// <summary>422: the period starts after it ends, or lasts more than a year (no summary could ever show it).</summary>
    public const string PeriodInvalid = "tourist_tax_period_invalid";

    /// <summary>422: the receipt is not a PDF, a JPEG or a PNG, is empty or is larger than <see cref="TouristTaxRemittanceLimits.MaxReceiptBytes"/>.</summary>
    public const string ReceiptInvalid = "tourist_tax_receipt_invalid";

    /// <summary>422: the reference or the note is longer than its column.</summary>
    public const string TextTooLong = "tourist_tax_text_too_long";

    /// <summary>404: no payment with that id in the caller's org (another org's is the same answer).</summary>
    public const string RemittanceNotFound = "tourist_tax_remittance_not_found";

    /// <summary>404: the payment has no receipt, or its file is no longer in the bucket.</summary>
    public const string ReceiptNotFound = "tourist_tax_receipt_not_found";

    /// <summary>
    /// 403: recording a payment, deleting one and opening its receipt need a scope that reaches the whole org. A payment belongs to the org and to
    /// no property, so a caller who reaches only some properties has no part in them.
    /// </summary>
    public const string PaymentsNeedWholeOrg = "tourist_tax_payments_need_whole_org";
}

/// <summary>The kind of file a receipt really is, read from its content (<see cref="TouristTaxReceiptRules.Sniff"/>).</summary>
/// <param name="ContentType">The media type to store and to serve.</param>
/// <param name="Extension">The extension to name the file with, lower case, with the dot.</param>
public sealed record TouristTaxReceiptKind(string ContentType, string Extension);

/// <summary>The rules of the summary and of the recording of the tourist tax payments, apart from any storage (NF-100).</summary>
public static class TouristTaxSummaryRules
{
    /// <summary>
    /// The status of a comune from the cents owed and the cents paid: more paid than owed is <see cref="TouristTaxSummaryStatus.Overpaid"/>
    /// (also with nothing owed), nothing owed and nothing paid is <see cref="TouristTaxSummaryStatus.NothingOwed"/>, the same amount to the cent
    /// is <see cref="TouristTaxSummaryStatus.Paid"/>, and the rest <see cref="TouristTaxSummaryStatus.ToPay"/>.
    /// </summary>
    public static TouristTaxSummaryStatus StatusOf(long owedCents, long paidCents)
    {
        if (paidCents > owedCents)
            return TouristTaxSummaryStatus.Overpaid;

        if (owedCents == 0)
            return TouristTaxSummaryStatus.NothingOwed;

        return paidCents == owedCents ? TouristTaxSummaryStatus.Paid : TouristTaxSummaryStatus.ToPay;
    }

    /// <summary>Whole cents as euros, with two decimals.</summary>
    public static decimal FromCents(long cents) => decimal.Round(cents / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The period of a summary: the one of the tourist tax report (<see cref="FiscalReportPeriod.IsValid"/>, at most a year), inside the years
    /// the reports support (2000 to 2100), so a typing mistake is a clear 400 and never an overflow of the calendar.
    /// </summary>
    public static bool IsSummaryPeriod(FiscalReportPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        return period.IsValid && period.From.Year >= MinYear && period.To.Year <= MaxYear;
    }

    /// <summary>
    /// True when <paramref name="amount"/> is a payment: above zero, at most <see cref="TouristTaxRemittanceLimits.MaxPaidAmount"/> and a
    /// whole number of cents (a third decimal that is not zero is a mistake, never rounded away).
    /// </summary>
    public static bool IsValidAmount(decimal amount) =>
        amount > 0m && amount <= TouristTaxRemittanceLimits.MaxPaidAmount && decimal.Round(amount, 2, MidpointRounding.AwayFromZero) == amount;

    /// <summary>
    /// Reads an amount sent as text: digits with, at most, one dot and two decimals (<c>12</c>, <c>12.5</c>, <c>12.50</c>), read with the invariant
    /// culture. No sign, no thousands separator, no comma, no exponent: <c>1.250,00</c> and <c>12,50</c> are refused rather than guessed (a form
    /// is read with the culture of the request, where the Italian dot is a thousands separator).
    /// </summary>
    public static bool TryParseAmount(string? text, out decimal amount)
    {
        amount = 0m;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > MaxAmountTextLength)
            return false;

        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot == 0 || dot == value.Length - 1 || (dot >= 0 && value.Length - dot - 1 > 2))
            return false;

        if (value.Any(c => !char.IsAsciiDigit(c) && c != '.') || value.Count(c => c == '.') > 1)
            return false;

        return decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    /// <summary>
    /// The period a payment pays: from the first day to the last included, not backwards, at most a year (as the summary can ask), inside the
    /// supported years. A longer one could never lie inside the period of a summary, and so would never be shown.
    /// </summary>
    public static bool IsValidRemittancePeriod(DateOnly from, DateOnly to) => IsSummaryPeriod(new FiscalReportPeriod(from, to));

    /// <summary>The comparable form of a comune name, the key payments and stays meet on (<see cref="TouristTaxComune.NormalizeName"/>).</summary>
    public static string ComuneKeyOf(string? comune) => TouristTaxComune.NormalizeName(comune);

    /// <summary>
    /// A text of the host trimmed, without the control characters that are not a tab or a line break (a NUL byte is refused by the database
    /// and would turn a typing mistake into a 500); <c>null</c> when nothing is left.
    /// </summary>
    public static string? CleanText(string? text)
    {
        if (text is null)
            return null;

        var value = new string(text.Where(c => !char.IsControl(c) || c is '\t' or '\n' or '\r').ToArray()).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// The name a receipt is stored and downloaded under: the base name the host sent (no folders, no control characters, at most
    /// <see cref="TouristTaxRemittance.ReceiptFileNameMaxLength"/> with the extension) and the extension of what the file really is, so a PDF
    /// is never offered as <c>ricevuta.exe</c>. <c>ricevuta</c> when the host sent no usable name.
    /// </summary>
    public static string ReceiptDisplayName(string? sentName, TouristTaxReceiptKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);

        var name = sentName ?? string.Empty;
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
            name = name[(slash + 1)..];

        var dot = name.LastIndexOf('.');
        if (dot > 0)
            name = name[..dot];

        var cleaned = new string(name.Where(c => !char.IsControl(c) && c != '"').ToArray()).Trim().Trim('.');
        if (cleaned.Length == 0)
            cleaned = "ricevuta";

        var room = TouristTaxRemittance.ReceiptFileNameMaxLength - kind.Extension.Length;
        if (cleaned.Length > room)
            cleaned = cleaned[..room].TrimEnd();

        return cleaned + kind.Extension;
    }

    private const int MinYear = 2000;
    private const int MaxYear = 2100;
    private const int MaxAmountTextLength = 16;
}

/// <summary>What a receipt is, from its first bytes (not from its name and not from the type the client declared).</summary>
public static class TouristTaxReceiptRules
{
    private static readonly byte[] Pdf = "%PDF-"u8.ToArray();
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The PDF kind.</summary>
    public static readonly TouristTaxReceiptKind PdfKind = new("application/pdf", ".pdf");

    /// <summary>The JPEG kind.</summary>
    public static readonly TouristTaxReceiptKind JpegKind = new("image/jpeg", ".jpg");

    /// <summary>The PNG kind.</summary>
    public static readonly TouristTaxReceiptKind PngKind = new("image/png", ".png");

    /// <summary>The kind of a receipt with these bytes: a PDF (<c>%PDF-</c>), a JPEG or a PNG by their signatures; <c>null</c> for anything else.</summary>
    public static TouristTaxReceiptKind? Sniff(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(Pdf))
            return PdfKind;

        if (content.StartsWith(Jpeg))
            return JpegKind;

        return content.StartsWith(Png) ? PngKind : null;
    }
}
