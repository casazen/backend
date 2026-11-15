using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Owner portal data access service (ADR-011).
/// Filters by PropertyOwnership.OwnerUserId and active ownership period.
/// All responses mask guest data per ADR-011-R3.
/// </summary>
public interface IOwnerPortalService
{
    /// <summary>
    /// Gets properties owned by the owner with occupancy and revenue summaries (ADR-011-R2, ADR-011-R8).
    /// Filters by ownership period: OwnershipStart &lt;= now AND (OwnershipEnd == null OR OwnershipEnd &gt;= now).
    /// Guest data is masked in booking summaries.
    /// </summary>
    Task<IReadOnlyList<OwnerPortalPropertyInfo>> GetOwnerPropertiesAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The statements the owner portal shows (E13/F01/US02/T02, <c>GET api/owner-portal/statements</c>): the published versions of the statements of the owner profiles the
    /// caller holds <c>accounting.read</c> on, the newest first, each with its period, version, currency, publication day and totals; the ones a later version superseded
    /// are marked. Behind <c>Features:AgencyTier</c>: with the flag off the list stays empty, as it was. A caller with no ownership in force gets an empty list; a
    /// delegated contact the owner did not give <c>accounting.read</c> is refused (403 <c>owner_portal_permission_missing</c>). Never a guest.
    /// </summary>
    Task<IReadOnlyList<OwnerPortalStatementInfo>> GetOwnerStatementsAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One published statement with its lines grouped as in its PDF (<c>GET api/owner-portal/statements/{id}</c>). 404 <c>owner_statement_version_not_found</c> for a
    /// statement that is not published, not the caller's, of another owner or missing (one answer for all); 403 as <see cref="GetOwnerStatementsAsync"/>.
    /// </summary>
    Task<OwnerPortalStatementDetailInfo> GetOwnerStatementAsync(
        Guid ownerUserId,
        Guid statementId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The stored PDF of a published statement, exactly what was published (<c>GET api/owner-portal/statements/{id}/pdf</c>); the download is written first. The caller
    /// disposes the stream. 404 as <see cref="GetOwnerStatementAsync"/>, and <c>owner_statement_pdf_not_available</c> when the version has no stored file.
    /// </summary>
    Task<OwnerFileContent> OpenOwnerStatementPdfAsync(
        Guid ownerUserId,
        Guid statementId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The invoices of the property manager to the owner (<c>GET api/owner-portal/invoices</c>, E13/F01/US02/T02): the issued documents with the purpose owner invoice
    /// whose recipient is an owner profile the caller holds <c>accounting.read</c> on, the newest first. Same flag, same empty list and same 403 as
    /// <see cref="GetOwnerStatementsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<OwnerPortalInvoiceInfo>> GetOwnerInvoicesAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The PDF of one invoice of the owner (<c>GET api/owner-portal/invoices/{id}/pdf</c>), made from the document as it was issued, in <paramref name="language"/> (Italian
    /// by default). 404 <c>owner_portal_invoice_not_found</c> for an invoice that is not the caller's, of another owner or missing.
    /// </summary>
    Task<OwnerFileContent> OpenOwnerInvoicePdfAsync(
        Guid ownerUserId,
        Guid invoiceId,
        string? language = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The documents CasaZen issued in the name of the owner (<c>GET api/owner-portal/issued-documents</c>, E17/F02/US01/T01, NF-116): the issued documents of the stays (a receipt of a
    /// private owner, the invoice of a company, their credit notes) whose issuer is the issuer profile of an owner profile the caller holds <c>accounting.read</c> on, the newest first,
    /// at most <see cref="OwnerCollaborationRules.MaxList"/>, narrowed to the ones issued in <paramref name="period"/> when it is given. Each with its number, day, kind, total, currency,
    /// state of the SDI and whether it has an XML; nothing of a guest. Behind <c>Features:AgencyTier</c> and <c>Features:FiscalDocumentIssuance</c>: with either off the list stays empty. A caller with no ownership in force gets an empty list; a delegated contact the owner did not give
    /// <c>accounting.read</c> is refused (403 <c>owner_portal_permission_missing</c>).
    /// </summary>
    Task<IReadOnlyList<OwnerPortalIssuedDocumentInfo>> GetOwnerIssuedDocumentsAsync(
        Guid ownerUserId,
        AccountantPackagePeriod? period = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The PDF of one document issued in the name of the owner (<c>GET api/owner-portal/issued-documents/{id}/pdf</c>), made from the document and the issuer as they were at the issue, in
    /// <paramref name="language"/> (Italian by default). 404 <c>owner_issued_document_not_found</c> for a document that is not one of the caller's (another owner's, the agency's, a
    /// draft, missing: one answer) and with a flag off; 403 as <see cref="GetOwnerIssuedDocumentsAsync"/>.
    /// </summary>
    Task<OwnerFileContent> OpenOwnerIssuedDocumentPdfAsync(
        Guid ownerUserId,
        Guid documentId,
        string? language = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The FatturaPA XML of one invoice or credit note issued in the name of the owner (<c>GET api/owner-portal/issued-documents/{id}/xml</c>), as it was stored at the issue. 404 as
    /// <see cref="OpenOwnerIssuedDocumentPdfAsync"/>, and <c>fiscal_document_xml_unavailable</c> for a document of the owner that has none (a receipt); 403 as the list.
    /// </summary>
    Task<OwnerFileContent> OpenOwnerIssuedDocumentXmlAsync(
        Guid ownerUserId,
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The ZIP for the accountant of the owner (<c>GET api/owner-portal/issued-documents/export?period=yyyy-MM|yyyy</c>), streamed to <paramref name="destination"/>: the PDF and the XML
    /// of the documents issued in the owner's name in the month or the year, their register and an index with the SHA-256 of every file, and for a year also their summary and the invoices
    /// of the property manager to the owner with their recharges. Every owner profile the caller holds <c>accounting.read</c> on (one folder each when more than one). 404
    /// <c>owner_issued_documents_not_available</c> with a flag off or with no ownership in force; 403 as the list; 422 <c>accountant_package_too_large</c> before the first byte.
    /// </summary>
    Task<AccountantPackageResult> ExportOwnerIssuedDocumentsAsync(
        Guid ownerUserId,
        AccountantPackagePeriod period,
        Stream destination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets withholding tax summary for the owner (ADR-011-R2).
    /// Returns empty summary for now (withholding feature not yet implemented).
    /// </summary>
    Task<OwnerPortalWithholdingInfo> GetOwnerWithholdingAsync(
        Guid ownerUserId,
        int? year = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies that the owner has access to the specified property (ADR-011-R2).
    /// Returns true if PropertyOwnership exists with active period, false otherwise.
    /// </summary>
    Task<bool> OwnerHasAccessToPropertyAsync(
        Guid ownerUserId,
        Guid propertyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The performance of the owner's properties on the Home's numbers (NF-96, <c>GET /api/owner-portal/overview</c>): occupancy,
    /// revenue, ADR and RevPAR, and next to them the same calendar days one year earlier when asked. The figures are those of
    /// <see cref="IHostDashboardService.GetPerformanceAsync"/> (the Home's stays, nights and rules, never a copy of them), read for
    /// each org the owner has properties in and <b>weighted by the share the owner holds</b> (<c>SharePercent</c>), so the owners of a
    /// co-owned property together are the Home's figure for it. Only the owner's own ownerships count: the ones in force today
    /// (<c>OwnershipStart</c> up to now, <c>OwnershipEnd</c> null or not before now) that started by the last day of the report and
    /// had not ended before its first. Guest data is never read.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">
    /// <see cref="OwnerOverviewQuery.PropertyId"/> is not one of the owner's properties in those days (another owner's, an ended
    /// ownership, missing): the same 404 for all (ADR-011-R2).
    /// </exception>
    Task<OwnerPortalOverview> GetOwnerOverviewAsync(
        Guid ownerUserId,
        OwnerOverviewQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same figures week by week or month by month (NF-96, <c>GET /api/owner-portal/trend</c>), for all the owner's properties
    /// together or one line for each (at most <see cref="HostPerformanceRules.MaxTrendSeries"/>, the rest summed in <c>other</c>), each
    /// bucket next to its days one year earlier when asked. Same ownerships and same weights as <see cref="GetOwnerOverviewAsync"/>.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">As <see cref="GetOwnerOverviewAsync"/>.</exception>
    Task<OwnerPortalTrend> GetOwnerTrendAsync(
        Guid ownerUserId,
        OwnerOverviewQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What the owner portal asks for its overview and its trend (NF-96): the days (both included, Europe/Rome dates, checked by
/// <see cref="HostReportRules.CheckPeriod"/> before the read), how the lines are cut (<see cref="HostPerformanceGroupBy.None"/> or
/// <see cref="HostPerformanceGroupBy.Property"/>: an owner has no channel cut) and whether the same days one year earlier come too.
/// </summary>
/// <param name="From">First day, included.</param>
/// <param name="To">Last day, included.</param>
/// <param name="GroupBy">The cut of the rows or lines.</param>
/// <param name="CompareYear">Also work out the same calendar days one year earlier.</param>
public sealed record OwnerOverviewQuery(
    DateOnly From,
    DateOnly To,
    HostPerformanceGroupBy GroupBy = HostPerformanceGroupBy.None,
    bool CompareYear = false)
{
    /// <summary>Only this property; null = every property of the owner. It must be one of the owner's.</summary>
    public Guid? PropertyId { get; init; }
}

/// <summary>One property of the owner's overview, or its total: the Home's figures weighted by the owner's share.</summary>
/// <param name="Key">A stable key: the property id, or <c>total</c>.</param>
/// <param name="PropertyId">The property of a property row; null for the total.</param>
/// <param name="Label">The name of the property; the key for the total.</param>
/// <param name="Figures">The figures of the days asked.</param>
/// <param name="PreviousYear">The same calendar days one year earlier; null when the overview did not compare and when there was nothing to compare with.</param>
public sealed record OwnerPortalRow(
    string Key,
    Guid? PropertyId,
    string Label,
    OwnerPerformanceFigures Figures,
    OwnerPerformanceFigures? PreviousYear);

/// <summary>The owner's properties on the Home's numbers (NF-96).</summary>
/// <param name="Period">The days of the overview.</param>
/// <param name="PreviousYearPeriod">The same calendar days one year earlier; null unless the overview compared.</param>
/// <param name="Rows">A row for each property of the owner that is active or had revenue, by name; none unless cut by property.</param>
/// <param name="Total">All the owner's properties together, weighted by the owner's shares.</param>
public sealed record OwnerPortalOverview(
    HostDashboardPeriod Period,
    HostDashboardPeriod? PreviousYearPeriod,
    IReadOnlyList<OwnerPortalRow> Rows,
    OwnerPortalRow Total);

/// <summary>One line of the owner's trend: all the properties together, or one of them (or <c>other</c>, the sum of the rest).</summary>
/// <param name="Key">A stable key: the property id, <c>total</c> or <c>other</c>.</param>
/// <param name="Label">The name of the property; the key for the others.</param>
/// <param name="PropertyId">The property of a property line; null for any other.</param>
/// <param name="Points">One entry for each bucket, in order, weighted by the owner's shares.</param>
/// <param name="PreviousYear">The same buckets one year earlier, null where there was nothing to compare with; empty unless the trend compared.</param>
public sealed record OwnerPortalSeries(
    string Key,
    string Label,
    Guid? PropertyId,
    IReadOnlyList<OwnerPerformanceFigures> Points,
    IReadOnlyList<OwnerPerformanceFigures?> PreviousYear);

/// <summary>The owner's trend (NF-96): the weeks or months of the period, the same buckets as the host's trend.</summary>
public sealed record OwnerPortalTrend(
    HostDashboardPeriod Period,
    HostDashboardPeriod? PreviousYearPeriod,
    HostOverviewSeriesUnit Unit,
    IReadOnlyList<HostTrendBucket> Buckets,
    IReadOnlyList<OwnerPortalSeries> Series);

/// <summary>
/// Property information for owner portal with masked guest data (ADR-011-R3).
/// </summary>
public class OwnerPortalPropertyInfo
{
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int TotalBookings { get; set; }
    public decimal TotalRevenue { get; set; }
    public double OccupancyRate { get; set; }
    public IReadOnlyList<OwnerPortalBookingInfo> RecentBookings { get; set; } = [];
}

/// <summary>
/// Booking information with guest data masked per ADR-011-R3.
/// Names NULL, emails NULL, phones NULL. Only dates, channel, amounts.
/// </summary>
public class OwnerPortalBookingInfo
{
    public Guid BookingId { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public string Channel { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public BookingStatus Status { get; set; }
}

/// <summary>
/// A published statement as the owner portal lists it (E13/F01/US02/T02): the period, the version, the currency, when it was published and its totals. Nothing of a
/// guest, nothing of another owner.
/// </summary>
/// <param name="StatementId">The version of the statement.</param>
/// <param name="AgencyId">The agency that publishes it (an owner can have more than one).</param>
/// <param name="AgencyName">The name of the agency.</param>
/// <param name="Year">The year of the period.</param>
/// <param name="Month">The month of the period.</param>
/// <param name="Version">The number of the version: 1 for the first of the month, then one more each.</param>
/// <param name="Currency">The currency of the amounts.</param>
/// <param name="PublishedAt">When it was published (UTC).</param>
/// <param name="Superseded">True when a later version of the period replaced it and that one is published too (it is kept, as it was published): while the correction is only a draft, the published statement is still the one the owner was given.</param>
/// <param name="SupersededByStatementId">The version that replaced it, when that one is published too.</param>
/// <param name="HasPdf">True when its PDF was stored with it and is available.</param>
/// <param name="Totals">The gross, the channel commissions, the withholding, the commission, the charges and the net, and the balances.</param>
public sealed record OwnerPortalStatementInfo(
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
    OwnerStatementTotals Totals);

/// <summary>A published statement with what its PDF says: the lines grouped by part, the invoice of the property manager when one is linked, and the tourist tax box.</summary>
/// <param name="Statement">The statement as the list shows it.</param>
/// <param name="Content">What the statement says (the lines and the box).</param>
public sealed record OwnerPortalStatementDetailInfo(OwnerPortalStatementInfo Statement, OwnerStatementContent Content);

/// <summary>An invoice of the property manager to the owner (E13/F01/US02/T02): its number, day, kind, total and currency, and what it is made from.</summary>
/// <param name="InvoiceId">The fiscal document.</param>
/// <param name="AgencyId">The agency that issued it.</param>
/// <param name="AgencyName">The name of the agency.</param>
/// <param name="Number">The number, with its series.</param>
/// <param name="IssueDate">The day it was issued (a Europe/Rome calendar day).</param>
/// <param name="Kind">An invoice, a receipt or a credit note.</param>
/// <param name="Total">The total, in <paramref name="Currency"/>.</param>
/// <param name="Currency">The currency.</param>
/// <param name="PeriodYear">The year of the period it is about.</param>
/// <param name="PeriodMonth">The month of the period it is about.</param>
/// <param name="StatementId">The version of the statement it was made from, when it is linked to one.</param>
/// <param name="CreditedInFull">True when credit notes credited the whole of it.</param>
public sealed record OwnerPortalInvoiceInfo(
    Guid InvoiceId,
    Guid AgencyId,
    string AgencyName,
    string Number,
    DateOnly IssueDate,
    FiscalDocumentKind Kind,
    decimal Total,
    string Currency,
    int? PeriodYear,
    int? PeriodMonth,
    Guid? StatementId,
    bool CreditedInFull);

/// <summary>
/// A document issued in the name of the owner as the owner portal lists it (E17/F02/US01/T01): its number, day, kind, total, currency and state of the SDI, and whether it has an XML.
/// Nothing of a guest: the PDF and the XML hold what the document itself says.
/// </summary>
/// <param name="DocumentId">The fiscal document.</param>
/// <param name="AgencyId">The agency that issues it for the owner (an owner can have more than one).</param>
/// <param name="AgencyName">The name of the agency.</param>
/// <param name="Number">The number, with the series of the owner.</param>
/// <param name="IssueDate">The day it was issued (a Europe/Rome calendar day).</param>
/// <param name="Kind">An invoice, a receipt or a credit note.</param>
/// <param name="Total">The total, in <paramref name="Currency"/>.</param>
/// <param name="Currency">The currency.</param>
/// <param name="SdiStatus">One of <c>FiscalDocumentSdiStatuses</c>.</param>
/// <param name="HasXml">True when the document has a FatturaPA XML to download.</param>
public sealed record OwnerPortalIssuedDocumentInfo(
    Guid DocumentId,
    Guid AgencyId,
    string AgencyName,
    string Number,
    DateOnly IssueDate,
    FiscalDocumentKind Kind,
    decimal Total,
    string Currency,
    string SdiStatus,
    bool HasXml);

/// <summary>
/// Withholding tax summary (placeholder for future implementation).
/// </summary>
public class OwnerPortalWithholdingInfo
{
    public int Year { get; set; }
    public decimal TotalWithheld { get; set; }
    public IReadOnlyList<OwnerPortalWithholdingDetailInfo> Details { get; set; } = [];
}

/// <summary>
/// Withholding detail (placeholder for future implementation).
/// </summary>
public class OwnerPortalWithholdingDetailInfo
{
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public int Month { get; set; }
    public decimal AmountWithheld { get; set; }
}
