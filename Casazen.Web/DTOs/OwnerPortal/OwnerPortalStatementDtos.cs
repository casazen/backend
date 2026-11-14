using Casazen.Core.Services;

namespace Casazen.Web.DTOs.OwnerPortal;

/// <summary>
/// A published statement as the owner portal lists it (E13/F01/US02/T02, <c>GET api/owner-portal/statements</c>): the period, the version, the currency, when it
/// was published and its totals. Nothing of a guest and nothing of another owner.
/// </summary>
/// <param name="StatementId">The version of the statement.</param>
/// <param name="AgencyId">The agency that publishes it.</param>
/// <param name="AgencyName">The name of the agency.</param>
/// <param name="Year">The year of the period.</param>
/// <param name="Month">The month of the period.</param>
/// <param name="Version">The number of the version.</param>
/// <param name="Currency">The currency of every amount.</param>
/// <param name="PublishedAt">When it was published (UTC).</param>
/// <param name="Superseded">True when a later version of the period replaced it; it stays as it was published.</param>
/// <param name="SupersededByStatementId">The version that replaced it, when that one is published too.</param>
/// <param name="HasPdf">True when the PDF stored with it can be downloaded (<c>…/pdf</c>).</param>
/// <param name="Totals">The totals of the statement.</param>
public sealed record OwnerPortalStatementDto(
    Guid StatementId,
    Guid AgencyId,
    string AgencyName,
    int Year,
    int Month,
    int Version,
    string Currency,
    DateTime PublishedAt,
    bool Superseded,
    Guid? SupersededByStatementId,
    bool HasPdf,
    OwnerPortalStatementTotalsDto Totals)
{
    public static OwnerPortalStatementDto From(OwnerPortalStatementInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new OwnerPortalStatementDto(
            info.StatementId,
            info.AgencyId,
            info.AgencyName,
            info.Year,
            info.Month,
            info.Version,
            info.Currency,
            info.PublishedAt,
            info.Superseded,
            info.SupersededByStatementId,
            info.HasPdf,
            OwnerPortalStatementTotalsDto.From(info.Totals));
    }
}

/// <summary>
/// The totals of a statement, signed as the owner ledger writes them: positive is owed to the owner, the commissions, the withholding and the charges are negative.
/// The <c>net</c> is the total of the period (<c>gross</c> + <c>channelFees</c> + <c>withholding</c> + <c>commission</c> + <c>charges</c> + <c>corrections</c>); the
/// <c>closingBalance</c> is the <c>openingBalance</c> plus the <c>net</c> plus the <c>payments</c>.
/// </summary>
/// <param name="Gross">The gross of the stays without the tourist tax (the owner's share).</param>
/// <param name="ChannelFees">What the portals kept.</param>
/// <param name="Withholding">The withholding kept from the payouts.</param>
/// <param name="Commission">The commission of the property manager.</param>
/// <param name="Charges">The recharges and the expenses.</param>
/// <param name="Corrections">The manual corrections.</param>
/// <param name="Net">The total of the period.</param>
/// <param name="Payments">The payments between the agency and the owner.</param>
/// <param name="OpeningBalance">The balance of the months before.</param>
/// <param name="ClosingBalance">What the agency owes the owner at the end of the month.</param>
public sealed record OwnerPortalStatementTotalsDto(
    decimal Gross,
    decimal ChannelFees,
    decimal Withholding,
    decimal Commission,
    decimal Charges,
    decimal Corrections,
    decimal Net,
    decimal Payments,
    decimal OpeningBalance,
    decimal ClosingBalance)
{
    public static OwnerPortalStatementTotalsDto From(OwnerStatementTotals totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        return new OwnerPortalStatementTotalsDto(
            totals.Gross,
            totals.ChannelFees,
            totals.Withholding,
            totals.Commission,
            totals.Recharges,
            totals.Corrections,
            totals.Period,
            totals.Payments,
            totals.OpeningBalance,
            totals.ClosingBalance);
    }
}

/// <summary>
/// A statement with its lines grouped as in its PDF (<c>GET api/owner-portal/statements/{id}</c>): the parts in the order of the file, the invoice of the property
/// manager when one is linked, and the tourist tax box, which is for information only and never part of the net. No guest: the stays are by property, days, channel
/// and booking code.
/// </summary>
/// <param name="Statement">The statement as the list shows it.</param>
/// <param name="OwnerName">The name the agency keeps for the owner.</param>
/// <param name="OwnerInvoiceNumber">The number of the invoice of the property manager to the owner for this statement, when one is issued.</param>
/// <param name="OwnerInvoiceDate">The day that invoice was issued.</param>
/// <param name="EntriesSha256">The SHA-256 of the ledger lines the statement was cut from.</param>
/// <param name="Sections">The parts: <c>stays</c>, <c>channelFees</c>, <c>withholding</c>, <c>commission</c>, <c>recharges</c>, <c>corrections</c> and <c>payments</c>.</param>
/// <param name="TouristTax">The tourist tax collected by the agency for the stays of the statement.</param>
public sealed record OwnerPortalStatementDetailDto(
    OwnerPortalStatementDto Statement,
    string OwnerName,
    string? OwnerInvoiceNumber,
    DateOnly? OwnerInvoiceDate,
    string EntriesSha256,
    IReadOnlyList<OwnerPortalStatementSectionDto> Sections,
    IReadOnlyList<OwnerPortalTouristTaxDto> TouristTax)
{
    public static OwnerPortalStatementDetailDto From(OwnerPortalStatementDetailInfo detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var content = detail.Content;
        return new OwnerPortalStatementDetailDto(
            OwnerPortalStatementDto.From(detail.Statement),
            content.OwnerName,
            content.OwnerInvoiceNumber,
            content.OwnerInvoiceDate,
            content.EntriesSha256,
            [.. OwnerPortalSectionNames.All.Select(section => OwnerPortalStatementSectionDto.From(section, content.Of(section)))],
            [.. content.TouristTax.Select(OwnerPortalTouristTaxDto.From)]);
    }
}

/// <summary>The part names the client sees, in the order of the PDF.</summary>
internal static class OwnerPortalSectionNames
{
    public static IReadOnlyList<OwnerStatementSection> All { get; } = Enum.GetValues<OwnerStatementSection>();

    public static string NameOf(OwnerStatementSection section)
    {
        return section switch
        {
            OwnerStatementSection.Stays => "stays",
            OwnerStatementSection.ChannelFees => "channelFees",
            OwnerStatementSection.Withholding => "withholding",
            OwnerStatementSection.Commission => "commission",
            OwnerStatementSection.Recharges => "recharges",
            OwnerStatementSection.Corrections => "corrections",
            OwnerStatementSection.Payments => "payments",
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Not a part of the owner statement."),
        };
    }
}

/// <summary>One part of a statement: its name, its total and its lines.</summary>
/// <param name="Section">One of <c>stays</c>, <c>channelFees</c>, <c>withholding</c>, <c>commission</c>, <c>recharges</c>, <c>corrections</c>, <c>payments</c>.</param>
/// <param name="Total">The sum of its lines.</param>
/// <param name="Lines">The lines, by day.</param>
public sealed record OwnerPortalStatementSectionDto(string Section, decimal Total, IReadOnlyList<OwnerPortalStatementLineDto> Lines)
{
    public static OwnerPortalStatementSectionDto From(OwnerStatementSection section, IReadOnlyList<OwnerStatementLine> lines) =>
        new(OwnerPortalSectionNames.NameOf(section), lines.Sum(line => line.Amount), [.. lines.Select(OwnerPortalStatementLineDto.From)]);
}

/// <summary>One line of a statement. No guest, never a name, an e-mail or a phone.</summary>
/// <param name="Id">The ledger line.</param>
/// <param name="Type">The type of the ledger line (<c>BookingRevenue</c>, <c>ChannelFee</c>, <c>AgencyCommission</c>, <c>OwnerCharge</c>...).</param>
/// <param name="Date">The day the line counts for the owner.</param>
/// <param name="CheckIn">The check-in of the stay the line is about.</param>
/// <param name="CheckOut">The check-out of that stay.</param>
/// <param name="Property">The name of the property.</param>
/// <param name="Channel">The channel of the stay.</param>
/// <param name="BookingCode">The code of the booking.</param>
/// <param name="Kind">The kind of a flat fee: <c>cleaning</c> or <c>linen</c>.</param>
/// <param name="Description">The words of the agency for a charge, an expense or a correction.</param>
/// <param name="SharePercent">The part of the source the owner holds.</param>
/// <param name="Amount">The signed amount: positive is owed to the owner.</param>
/// <param name="Reversal">True when the line reverses another.</param>
/// <param name="ChannelFeeRechargedByAgency">True when the portal billed the agency, which recharges the commission to the owner.</param>
public sealed record OwnerPortalStatementLineDto(
    Guid Id,
    string Type,
    DateOnly Date,
    DateOnly? CheckIn,
    DateOnly? CheckOut,
    string? Property,
    string? Channel,
    string? BookingCode,
    string? Kind,
    string? Description,
    decimal SharePercent,
    decimal Amount,
    bool Reversal,
    bool ChannelFeeRechargedByAgency)
{
    public static OwnerPortalStatementLineDto From(OwnerStatementLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new OwnerPortalStatementLineDto(
            line.EntryId,
            line.Type.ToString(),
            line.Date,
            line.CheckIn,
            line.CheckOut,
            line.PropertyName,
            line.Channel,
            line.BookingCode,
            line.Reference,
            line.Description,
            line.SharePercent,
            line.Amount,
            line.Reversal,
            line.AgencyHoldsPortalAccount);
    }
}

/// <summary>The tourist tax of one stay of the statement, collected by the agency: for information only, outside the net.</summary>
/// <param name="Property">The name of the property.</param>
/// <param name="CheckIn">The check-in of the stay.</param>
/// <param name="CheckOut">The check-out of the stay.</param>
/// <param name="Channel">The channel of the stay.</param>
/// <param name="BookingCode">The code of the booking.</param>
/// <param name="Currency">The currency the tax was collected in.</param>
/// <param name="Amount">The tax of the whole stay.</param>
public sealed record OwnerPortalTouristTaxDto(
    string? Property,
    DateOnly CheckIn,
    DateOnly CheckOut,
    string? Channel,
    string? BookingCode,
    string Currency,
    decimal Amount)
{
    public static OwnerPortalTouristTaxDto From(OwnerStatementTouristTax tax)
    {
        ArgumentNullException.ThrowIfNull(tax);
        return new OwnerPortalTouristTaxDto(tax.PropertyName, tax.CheckIn, tax.CheckOut, tax.Channel, tax.BookingCode, tax.Currency, tax.Amount);
    }
}

/// <summary>
/// An invoice of the property manager to the owner (<c>GET api/owner-portal/invoices</c>, E13/F01/US02/T02): the number, the day, the kind, the total and the
/// currency, and the period and the statement it is made from. The PDF is at <c>…/invoices/{id}/pdf</c>.
/// </summary>
/// <param name="InvoiceId">The fiscal document.</param>
/// <param name="AgencyId">The agency that issued it.</param>
/// <param name="AgencyName">The name of the agency.</param>
/// <param name="Number">The number, with its series.</param>
/// <param name="IssueDate">The day it was issued.</param>
/// <param name="Kind">The kind of document: <c>Invoice</c>, <c>Receipt</c> or <c>CreditNote</c>.</param>
/// <param name="Total">The total.</param>
/// <param name="Currency">The currency.</param>
/// <param name="PeriodYear">The year of the period it is about.</param>
/// <param name="PeriodMonth">The month of the period it is about.</param>
/// <param name="StatementId">The version of the statement it was made from, when it is linked to one.</param>
/// <param name="CreditedInFull">True when credit notes credited the whole of it.</param>
public sealed record OwnerPortalInvoiceDto(
    Guid InvoiceId,
    Guid AgencyId,
    string AgencyName,
    string Number,
    DateOnly IssueDate,
    string Kind,
    decimal Total,
    string Currency,
    int? PeriodYear,
    int? PeriodMonth,
    Guid? StatementId,
    bool CreditedInFull)
{
    public static OwnerPortalInvoiceDto From(OwnerPortalInvoiceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new OwnerPortalInvoiceDto(
            info.InvoiceId,
            info.AgencyId,
            info.AgencyName,
            info.Number,
            info.IssueDate,
            info.Kind.ToString(),
            info.Total,
            info.Currency,
            info.PeriodYear,
            info.PeriodMonth,
            info.StatementId,
            info.CreditedInFull);
    }
}
