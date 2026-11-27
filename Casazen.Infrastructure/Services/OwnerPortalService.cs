using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Owner portal data access service (ADR-011).
/// Filters by PropertyOwnership.OwnerUserId and active ownership period (ADR-011-R2, ADR-011-R8).
/// All responses mask guest data per ADR-011-R3: names NULL, emails NULL, phones NULL.
/// </summary>
/// <param name="dashboard">
/// The Home's numbers (NF-96): the occupancy of the property list and the overview and trend read them. The dependency injection always
/// passes it; it is optional only so that the places that build this service for the three older reads keep compiling.
/// </param>
/// <param name="featureFlags">
/// The flag of the agency tier, behind which the statements and the invoices of the owner are shown (E13/F01/US02/T02). Optional for the same reason as
/// <paramref name="dashboard"/>: the places that build this service for the older reads keep compiling; the dependency injection always passes it.
/// </param>
/// <param name="accessResolver">What the caller reaches now (its ownerships and the permissions a delegation gave): who may read the accounting.</param>
/// <param name="statementDocuments">The statements as the PDF has them, the stored PDF and the PDF of an invoice.</param>
/// <param name="issuedDocuments">The documents CasaZen issued in the name of the owner: the list, their PDF and XML and the ZIP for the owner's accountant (E17/F02/US01/T01); optional for the same reason as <paramref name="dashboard"/>.</param>
public sealed class OwnerPortalService(
    AppDbContext db,
    TimeProvider timeProvider,
    IHostDashboardService? dashboard = null,
    IFeatureFlags? featureFlags = null,
    IOwnerPortalAccessResolver? accessResolver = null,
    IOwnerStatementDocuments? statementDocuments = null,
    IOwnerIssuedDocumentsExport? issuedDocuments = null) : IOwnerPortalService
{
    /// <summary>
    /// Gets properties owned by the owner with occupancy and revenue summaries (ADR-011-R2, ADR-011-R8).
    /// Filters by ownership period: OwnershipStart &lt;= now AND (OwnershipEnd == null OR OwnershipEnd &gt;= now).
    /// Guest data is masked in booking summaries (ADR-011-R3).
    /// </summary>
    public async Task<IReadOnlyList<OwnerPortalPropertyInfo>> GetOwnerPropertiesAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Get active property ownerships for this owner (ADR-011-R2, ADR-011-R8). The caller of the portal has no org of its own: the tenant filter of its
        // request is fail-closed and would match nothing, so every read here ignores it and names the owner, the org and the property explicitly (the soft
        // delete filter of the properties stays).
        var propertyIds = await Ownerships()
            .Where(po => po.OwnerUserId == ownerUserId
                      && po.OwnershipStart <= now
                      && (po.OwnershipEnd == null || po.OwnershipEnd >= now))
            .Select(po => po.PropertyId)
            .ToListAsync(cancellationToken);

        if (!propertyIds.Any())
            return [];

        // Get properties with their bookings. The subquery is built outside the projection: an expression tree cannot hold the list of the filters to ignore.
        var bookings = db.Set<Booking>().IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);
        var properties = await db.Set<Property>()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(p => propertyIds.Contains(p.Id))
            .Select(p => new OwnerPortalPropertyInfo
            {
                PropertyId = p.Id,
                PropertyName = p.Name,
                Address = p.Address,
                TotalBookings = bookings
                    .Count(b => b.OrgId == p.OrgId && b.PropertyId == p.Id && b.Status != BookingStatus.Cancelled),
                TotalRevenue = bookings
                    .Where(b => b.OrgId == p.OrgId && b.PropertyId == p.Id && b.Status != BookingStatus.Cancelled)
                    .Sum(b => (decimal?)b.TotalPrice) ?? 0m,
                RecentBookings = bookings
                    .Where(b => b.OrgId == p.OrgId && b.PropertyId == p.Id && b.Status != BookingStatus.Cancelled)
                    .OrderByDescending(b => b.CheckInDate)
                    .Take(10)
                    .Select(b => new OwnerPortalBookingInfo
                    {
                        BookingId = b.Id,
                        CheckInDate = b.CheckInDate,
                        CheckOutDate = b.CheckOutDate,
                        Channel = b.Source.ToString(),
                        TotalAmount = b.TotalPrice,
                        Status = b.Status
                        // Guest data masked: no names, emails, phones per ADR-011-R3
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        // NF-96: the occupancy of the last 30 days is the Home's (the very nights the Home counts for «ultimi 30 giorni»), as a percentage
        // from 0 to 100 to one decimal, as the page prints it; it used to be a placeholder of 0.
        if (dashboard is not null)
        {
            var occupancy = await dashboard.GetLast30DaysOccupancyAsync(propertyIds, cancellationToken);
            foreach (var property in properties)
            {
                if (occupancy.TryGetValue(property.PropertyId, out var nights) && nights.Rate is { } rate)
                    property.OccupancyRate = (double)Math.Round(rate * 100m, 1, MidpointRounding.AwayFromZero);
            }
        }

        return properties;
    }

    // ---------------------------------------------------------------- E13/F01/US02/T02: the statements and the invoices of the owner

    public async Task<IReadOnlyList<OwnerPortalStatementInfo>> GetOwnerStatementsAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        if (await AccountingAccessAsync(ownerUserId, cancellationToken) is not { } access)
            return [];

        // The published versions of the owner profiles the caller reads the accounting of; the newest first. The filter on the grants is in memory: the owner profile
        // ids are the caller's, and a contact that holds the permission on some of an owner's properties reads the statements of that owner profile.
        var ownerIds = access.With(OwnerPortalPermission.AccountingRead).Select(g => g.PropertyOwnerId).Distinct().ToList();
        var versions = (await StatementVersions()
                .Where(v => ownerIds.Contains(v.PropertyOwnerId) && v.PublishedAt != null)
                .OrderByDescending(v => v.PeriodYear)
                .ThenByDescending(v => v.PeriodMonth)
                .ThenByDescending(v => v.VersionNumber)
                .Take(OwnerCollaborationRules.MaxList)
                .ToListAsync(cancellationToken))
            .Where(v => access.Covers(v.OrgId, v.PropertyOwnerId, OwnerPortalPermission.AccountingRead))
            .ToList();
        return await StatementSummariesAsync(versions, cancellationToken);
    }

    public async Task<OwnerPortalStatementDetailInfo> GetOwnerStatementAsync(
        Guid ownerUserId,
        Guid statementId,
        CancellationToken cancellationToken = default)
    {
        var version = await ReachedStatementAsync(ownerUserId, statementId, cancellationToken);
        var summary = (await StatementSummariesAsync([version], cancellationToken))[0];
        var content = await StatementFiles().GetContentAsync(version.OrgId, version.Id, null, cancellationToken);
        return new OwnerPortalStatementDetailInfo(summary, content);
    }

    public async Task<OwnerFileContent> OpenOwnerStatementPdfAsync(
        Guid ownerUserId,
        Guid statementId,
        CancellationToken cancellationToken = default)
    {
        var version = await ReachedStatementAsync(ownerUserId, statementId, cancellationToken);
        return await StatementFiles().OpenPublishedPdfAsync(version.OrgId, version.Id, ownerUserId, cancellationToken)
            ?? throw new NotFoundException($"The PDF of owner statement {statementId} is not available")
            {
                Code = OwnerStatementFileErrors.PdfNotAvailable,
                MessageKey = OwnerStatementFileErrors.PdfNotAvailableMessageKey,
            };
    }

    public async Task<IReadOnlyList<OwnerPortalInvoiceInfo>> GetOwnerInvoicesAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        if (await AccountingAccessAsync(ownerUserId, cancellationToken) is not { } access)
            return [];

        // The issued documents the property manager made out to the owner profiles of the caller. The recipient snapshot (an address, a tax id) is never read.
        var ownerIds = access.With(OwnerPortalPermission.AccountingRead).Select(g => g.PropertyOwnerId).Distinct().ToList();
        var rows = (await db.FiscalDocuments
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(d => d.Purpose == FiscalDocumentPurpose.OwnerInvoice && d.Status == FiscalDocumentStatus.Issued && d.Kind != FiscalDocumentKind.Proforma
                    && d.Number != null && d.IssueDate != null && d.RecipientOwnerId != null && ownerIds.Contains(d.RecipientOwnerId.Value))
                .OrderByDescending(d => d.IssueDate)
                .ThenByDescending(d => d.Number)
                .Take(OwnerCollaborationRules.MaxList)
                .Select(d => new
                {
                    d.Id,
                    d.OrgId,
                    Owner = d.RecipientOwnerId!.Value,
                    d.Number,
                    d.IssueDate,
                    d.Kind,
                    d.Total,
                    d.Currency,
                    d.PeriodYear,
                    d.PeriodMonth,
                    d.OwnerStatementVersionId,
                    d.CreditedInFull,
                })
                .ToListAsync(cancellationToken))
            .Where(d => access.Covers(d.OrgId, d.Owner, OwnerPortalPermission.AccountingRead))
            .ToList();
        var agencies = await AgencyNamesAsync(rows.Select(d => d.OrgId), cancellationToken);

        return rows
            .Select(d => new OwnerPortalInvoiceInfo(
                d.Id,
                d.OrgId,
                agencies.GetValueOrDefault(d.OrgId, string.Empty),
                d.Number!,
                d.IssueDate!.Value,
                d.Kind,
                d.Total,
                d.Currency,
                d.PeriodYear,
                d.PeriodMonth,
                d.OwnerStatementVersionId,
                d.CreditedInFull))
            .ToList();
    }

    public async Task<OwnerFileContent> OpenOwnerInvoicePdfAsync(
        Guid ownerUserId,
        Guid invoiceId,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        // One 404 for an invoice that is not the caller's, of another owner, not issued, not an owner invoice or missing.
        var access = await AccountingAccessAsync(ownerUserId, cancellationToken) ?? throw InvoiceNotFound(invoiceId);
        var document = await db.FiscalDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.Id == invoiceId && d.Purpose == FiscalDocumentPurpose.OwnerInvoice && d.Status == FiscalDocumentStatus.Issued)
            .Select(d => new { d.OrgId, d.RecipientOwnerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (document?.RecipientOwnerId is not { } owner || !access.Covers(document.OrgId, owner, OwnerPortalPermission.AccountingRead))
            throw InvoiceNotFound(invoiceId);

        var file = await StatementFiles().GetOwnerInvoicePdfAsync(document.OrgId, invoiceId, language, cancellationToken) ?? throw InvoiceNotFound(invoiceId);
        return new OwnerFileContent(new MemoryStream(file.Content, writable: false), file.FileName, file.ContentType);
    }

    // ---------------------------------------------------------------- E17/F02/US01/T01: the documents issued in the name of the owner

    public async Task<IReadOnlyList<OwnerPortalIssuedDocumentInfo>> GetOwnerIssuedDocumentsAsync(
        Guid ownerUserId,
        AccountantPackagePeriod? period = null,
        CancellationToken cancellationToken = default)
    {
        if (await IssuedDocumentsAccessAsync(ownerUserId, cancellationToken) is not { } access)
            return [];

        // Only the documents issued on the issuer profile of an owner profile the caller holds accounting.read on: the agency's own documents and the ones of another owner are not there.
        var rows = await IssuedDocuments().ListAsync(OwnersOf(access), period, OwnerCollaborationRules.MaxList, cancellationToken);
        var agencies = await AgencyNamesAsync(rows.Select(r => r.OrgId), cancellationToken);

        return
        [
            .. rows.Select(r => new OwnerPortalIssuedDocumentInfo(
                r.DocumentId,
                r.OrgId,
                agencies.GetValueOrDefault(r.OrgId, string.Empty),
                r.Number,
                r.IssueDate,
                r.Kind,
                r.Total,
                r.Currency,
                r.SdiStatus,
                r.HasXml)),
        ];
    }

    public async Task<OwnerFileContent> OpenOwnerIssuedDocumentPdfAsync(
        Guid ownerUserId,
        Guid documentId,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        // One 404 for a document that is not the caller's, of another owner, the agency's, not issued or missing, and with a flag off.
        var access = await IssuedDocumentsAccessAsync(ownerUserId, cancellationToken) ?? throw IssuedDocumentNotFound(documentId);
        var file = await IssuedDocuments().GetPdfAsync(OwnersOf(access), documentId, language, cancellationToken) ?? throw IssuedDocumentNotFound(documentId);
        return new OwnerFileContent(new MemoryStream(file.Content, writable: false), file.FileName, file.ContentType);
    }

    public async Task<OwnerFileContent> OpenOwnerIssuedDocumentXmlAsync(
        Guid ownerUserId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var access = await IssuedDocumentsAccessAsync(ownerUserId, cancellationToken) ?? throw IssuedDocumentNotFound(documentId);
        var file = await IssuedDocuments().GetXmlAsync(OwnersOf(access), documentId, cancellationToken) ?? throw IssuedDocumentNotFound(documentId);
        return new OwnerFileContent(new MemoryStream(file.Content, writable: false), file.FileName, file.ContentType);
    }

    public async Task<AccountantPackageResult> ExportOwnerIssuedDocumentsAsync(
        Guid ownerUserId,
        AccountantPackagePeriod period,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(destination);

        var access = await IssuedDocumentsAccessAsync(ownerUserId, cancellationToken)
            ?? throw new NotFoundException($"Nothing to export for owner user {ownerUserId}")
            {
                Code = OwnerIssuedDocumentsErrors.NotAvailable,
                MessageKey = OwnerIssuedDocumentsErrors.NotAvailableMessageKey,
            };
        return await IssuedDocuments().BuildAsync(OwnersOf(access), period, destination, cancellationToken);
    }

    /// <summary>
    /// What the caller reads of the documents issued in the name of its owners: the accounting access (<see cref="AccountingAccessAsync"/>: the agency plan, an ownership in force,
    /// <c>accounting.read</c>), and <c>null</c> too while <c>Features:FiscalDocumentIssuance</c> is off: with either flag off the lists stay empty and the files are not there.
    /// </summary>
    private async Task<OwnerPortalAccess?> IssuedDocumentsAccessAsync(Guid ownerUserId, CancellationToken cancellationToken)
    {
        if (!Features().IsEnabled(FeatureFlags.FiscalDocumentIssuance))
            return null;

        return await AccountingAccessAsync(ownerUserId, cancellationToken);
    }

    /// <summary>The owner profiles the caller holds <c>accounting.read</c> on, each of an agency: the owners whose documents it reads (never more than the grants say).</summary>
    private static IReadOnlyList<OwnerIssuedDocumentsOwner> OwnersOf(OwnerPortalAccess access) =>
        [.. access.With(OwnerPortalPermission.AccountingRead).Select(g => new OwnerIssuedDocumentsOwner(g.OrgId, g.PropertyOwnerId)).Distinct()];

    /// <summary>
    /// What the caller reads of the accounting. <c>null</c> when there is nothing to show: <c>Features:AgencyTier</c> is off (the lists stay empty as they were), or the
    /// caller has no ownership in force and no delegation. A caller that has some and holds <c>accounting.read</c> on none of them (a delegated contact the owner did not
    /// give it to) is refused, 403.
    /// </summary>
    private async Task<OwnerPortalAccess?> AccountingAccessAsync(Guid ownerUserId, CancellationToken cancellationToken)
    {
        if (!Features().IsEnabled(FeatureFlags.AgencyTier))
            return null;

        var access = await AccessResolver().ResolveAsync(ownerUserId, cancellationToken);
        if (access.Grants.Count == 0)
            return null;

        access.Require(OwnerPortalPermission.AccountingRead);
        return access;
    }

    /// <summary>A published version of the statements of an owner profile the caller reads the accounting of; the same 404 for one that is not published, not the caller's, of another owner or missing.</summary>
    private async Task<OwnerStatementVersion> ReachedStatementAsync(Guid ownerUserId, Guid statementId, CancellationToken cancellationToken)
    {
        var access = await AccountingAccessAsync(ownerUserId, cancellationToken) ?? throw StatementNotFound(statementId);
        var version = await StatementVersions().FirstOrDefaultAsync(v => v.Id == statementId && v.PublishedAt != null, cancellationToken);
        if (version is null || !access.Covers(version.OrgId, version.PropertyOwnerId, OwnerPortalPermission.AccountingRead))
            throw StatementNotFound(statementId);

        return version;
    }

    /// <summary>The statements as the list shows them: the agency, whether the PDF is stored, and the version that replaced it when that one is published too.</summary>
    private async Task<IReadOnlyList<OwnerPortalStatementInfo>> StatementSummariesAsync(IReadOnlyList<OwnerStatementVersion> versions, CancellationToken cancellationToken)
    {
        if (versions.Count == 0)
            return [];

        var ids = versions.Select(v => v.Id).ToList();
        var laterIds = versions.Where(v => v.SupersededByVersionId != null).Select(v => v.SupersededByVersionId!.Value).Distinct().ToList();
        var agencies = await AgencyNamesAsync(versions.Select(v => v.OrgId), cancellationToken);
        var withPdf = (await db.OwnerDocuments
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(d => ids.Contains(d.Id) && d.Type == OwnerDocumentType.Statement && d.RevokedAt == null)
                .Select(d => d.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var laterPublished = (await StatementVersions()
                .Where(v => laterIds.Contains(v.Id) && v.PublishedAt != null)
                .Select(v => v.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // For the owner a statement is replaced only when the version that replaces it is published too: the agency marks the published one as superseded as soon as it
        // generates a correction, but until that one is published (a draft, or approved and not yet given) the published statement is still the one the owner was given.
        Guid? ReplacementOf(OwnerStatementVersion version) => version.SupersededByVersionId is { } later && laterPublished.Contains(later) ? later : null;

        return versions
            .Select(v => new OwnerPortalStatementInfo(
                v.Id,
                v.OrgId,
                agencies.GetValueOrDefault(v.OrgId, string.Empty),
                v.PeriodYear,
                v.PeriodMonth,
                v.VersionNumber,
                v.Currency,
                v.PublishedAt!.Value,
                ReplacementOf(v) is not null,
                ReplacementOf(v),
                withPdf.Contains(v.Id),
                OwnerStatementTotals.Of(v)))
            .ToList();
    }

    private async Task<Dictionary<Guid, string>> AgencyNamesAsync(IEnumerable<Guid> orgIds, CancellationToken cancellationToken)
    {
        var ids = orgIds.Distinct().ToList();
        return await db.Orgs.AsNoTracking().Where(o => ids.Contains(o.Id)).Select(o => new { o.Id, o.Name }).ToDictionaryAsync(o => o.Id, o => o.Name, cancellationToken);
    }

    /// <summary>The statement versions of every org: the caller of the portal has no org of its own, so what it reaches is named by its grants in each predicate.</summary>
    private IQueryable<OwnerStatementVersion> StatementVersions() =>
        db.OwnerStatementVersions.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking();

    private IFeatureFlags Features() =>
        featureFlags ?? throw new InvalidOperationException("The owner portal accounting needs the feature flags, which the dependency injection provides.");

    private IOwnerPortalAccessResolver AccessResolver() =>
        accessResolver ?? throw new InvalidOperationException("The owner portal accounting needs the access resolver, which the dependency injection provides.");

    private IOwnerStatementDocuments StatementFiles() =>
        statementDocuments ?? throw new InvalidOperationException("The owner portal accounting needs the statement files, which the dependency injection provides.");

    private IOwnerIssuedDocumentsExport IssuedDocuments() =>
        issuedDocuments ?? throw new InvalidOperationException("The documents issued in the name of the owner need their export service, which the dependency injection provides.");

    private static NotFoundException StatementNotFound(Guid statementId) =>
        new($"Owner statement version {statementId} was not found")
        {
            Code = OwnerAccountingErrors.VersionNotFound,
            MessageKey = "OwnerStatementVersionNotFound",
        };

    private static NotFoundException InvoiceNotFound(Guid invoiceId) =>
        new($"Owner invoice {invoiceId} was not found")
        {
            Code = OwnerStatementFileErrors.InvoiceNotFound,
            MessageKey = OwnerStatementFileErrors.InvoiceNotFoundMessageKey,
        };

    private static NotFoundException IssuedDocumentNotFound(Guid documentId) =>
        new($"Owner issued document {documentId} was not found")
        {
            Code = OwnerIssuedDocumentsErrors.NotFound,
            MessageKey = OwnerIssuedDocumentsErrors.NotFoundMessageKey,
        };

    // ---------------------------------------------------------------- E18/F01/US02/T01: the withholding and the Certificazione Unica of the owner

    /// <summary>
    /// The withholding of the owner in the year (ADR-011-R2, E18/F01/US02/T01). <b>Only the owner profiles of the claim</b>: the evidence and the annual data are read by the owner profiles the
    /// caller holds <c>accounting.read</c> on (<see cref="AccountingAccessAsync"/>), each predicate names them and the tenant filter of the caller (it has no org of its own) is ignored on purpose,
    /// so another owner's withholding, or a co-owner's, is never read. The totals are the evidence's, to the cent: the pieces of a payout (<see cref="TaxEvidenceLines"/>) add up to it. The
    /// annual data of the year is shown once reviewed, and only the record of the profile (the totals: never the recipients, the stays or the co-owners it adds up).
    /// </summary>
    public async Task<OwnerPortalWithholdingInfo> GetOwnerWithholdingAsync(
        Guid ownerUserId,
        int? year = null,
        CancellationToken cancellationToken = default)
    {
        var taxYear = year ?? timeProvider.TodayInRomeAsDateOnly().Year;
        var summary = new OwnerPortalWithholdingInfo { Year = taxYear };

        // Both flags off (the production default): the empty summary it was.
        if (!Features().IsEnabled(FeatureFlags.TaxReportingExports) || await AccountingAccessAsync(ownerUserId, cancellationToken) is not { } access)
            return summary;

        var grants = access.With(OwnerPortalPermission.AccountingRead);
        var ownerIds = grants.Select(g => g.PropertyOwnerId).Distinct().ToList();
        var evidence = (await db.TaxObligationEvidence
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(e => e.Decision == TaxDecision.Applicable && e.PeriodYear == taxYear && ownerIds.Contains(e.PropertyOwnerId))
                .Where(OwnerPayoutService.IsItalianRule)
                .OrderBy(e => e.CreatedAt)
                .ThenBy(e => e.Id)
                .ToListAsync(cancellationToken))
            .Where(e => access.Covers(e.OrgId, e.PropertyOwnerId, OwnerPortalPermission.AccountingRead))
            .ToList();
        var lines = evidence.SelectMany(TaxEvidenceLines.Of).ToList();

        var orgIds = grants.Select(g => g.OrgId).Distinct().ToList();
        var agencies = await AgencyNamesAsync(orgIds, cancellationToken);
        var propertyIds = lines.Where(l => l.PropertyId is not null).Select(l => l.PropertyId!.Value).Distinct().ToList();
        var propertyNames = new Dictionary<Guid, string>();
        if (propertyIds.Count > 0)
        {
            propertyNames = await db.Set<Property>()
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter, AppDbContext.SoftDeleteQueryFilter])
                .AsNoTracking()
                .Where(p => propertyIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name })
                .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        }

        summary.TotalWithheld = evidence.Sum(e => e.Amount ?? 0m);
        summary.Details =
        [
            .. lines
                .GroupBy(l => (l.OrgId, l.Month, l.PropertyId))
                .Select(g => new OwnerPortalWithholdingDetailInfo
                {
                    AgencyId = g.Key.OrgId,
                    AgencyName = agencies.GetValueOrDefault(g.Key.OrgId, string.Empty),
                    PropertyId = g.Key.PropertyId ?? Guid.Empty,
                    PropertyName = g.Key.PropertyId is { } id ? propertyNames.GetValueOrDefault(id, string.Empty) : string.Empty,
                    Month = g.Key.Month,
                    GrossBase = g.Sum(l => l.GrossBase),
                    AmountWithheld = g.Sum(l => l.Withheld),
                })
                .Where(d => d.GrossBase != 0m || d.AmountWithheld != 0m)
                .OrderBy(d => d.Month)
                .ThenBy(d => d.AgencyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.PropertyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.PropertyId),
        ];
        summary.CuStatements = await CuStatementsAsync(grants, taxYear, agencies, cancellationToken);
        return summary;
    }

    /// <summary>
    /// The Certificazione Unica of the year of the owner profiles of <paramref name="grants"/>, from the annual data of each agency once it is reviewed (reviewed, submitted or accepted: the
    /// data still generated can be built again, and a rejected one is not the owner's to read). Only the record of each profile, as the totals.
    /// </summary>
    private async Task<IReadOnlyList<OwnerPortalCuInfo>> CuStatementsAsync(
        IReadOnlyList<OwnerPortalGrant> grants,
        int year,
        IReadOnlyDictionary<Guid, string> agencies,
        CancellationToken cancellationToken)
    {
        var orgIds = grants.Select(g => g.OrgId).Distinct().ToList();
        var datasets = await db.TaxReportingDatasets
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.Kind == TaxReportingDataset.CertificazioneUnica && d.Year == year && orgIds.Contains(d.OrgId))
            .ToListAsync(cancellationToken);

        var statements = new List<OwnerPortalCuInfo>();
        foreach (var dataset in datasets.Where(d => d.FilingState is TaxFilingState.Reviewed or TaxFilingState.Submitted or TaxFilingState.Accepted))
        {
            IReadOnlyList<CuRecord> records;
            try
            {
                records = CuDatasetPayload.Parse(dataset.PayloadJson);
            }
            catch (JsonException)
            {
                // A damaged payload shows nothing to the owner; the agency's own exports fail loudly on it.
                continue;
            }

            foreach (var ownerId in grants.Where(g => g.OrgId == dataset.OrgId).Select(g => g.PropertyOwnerId).Distinct())
            {
                if (records.FirstOrDefault(r => r.OwnerId == ownerId) is { } record)
                {
                    statements.Add(new OwnerPortalCuInfo(
                        dataset.OrgId,
                        agencies.GetValueOrDefault(dataset.OrgId, string.Empty),
                        dataset.Year,
                        dataset.FilingState,
                        dataset.GeneratedAt,
                        record.GrossBase,
                        record.Withheld,
                        record.TaxCode,
                        record.Attribution));
                }
            }
        }

        return statements.OrderBy(s => s.AgencyName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.AgencyId).ToList();
    }

    /// <summary>
    /// Verifies that the owner has access to the specified property (ADR-011-R2).
    /// Returns true if PropertyOwnership exists with active period, false otherwise.
    /// </summary>
    public async Task<bool> OwnerHasAccessToPropertyAsync(
        Guid ownerUserId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        return await Ownerships()
            .AnyAsync(po => po.OwnerUserId == ownerUserId
                         && po.PropertyId == propertyId
                         && po.OwnershipStart <= now
                         && (po.OwnershipEnd == null || po.OwnershipEnd >= now),
                      cancellationToken);
    }

    // ---------------------------------------------------------------- NF-96: the Home's numbers for the owner

    // A property of the owner and the part of it the owner holds (0..1): the weight of every figure of the Home for that property.
    private sealed record OwnedProperty(Guid OrgId, Guid PropertyId, decimal Weight);

    /// <summary>
    /// The performance of the owner's properties on the Home's numbers, weighted by the owner's shares (NF-96). The figures of each org
    /// come from <see cref="IHostDashboardService.GetPerformanceAsync"/> with that org's scope narrowed to the owner's properties; the
    /// total is the sum of the weighted rows. Guest data is never read.
    /// </summary>
    public async Task<OwnerPortalOverview> GetOwnerOverviewAsync(
        Guid ownerUserId,
        OwnerOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var host = HostDashboard();
        var owned = await OwnedPropertiesAsync(ownerUserId, query, cancellationToken);
        var period = HostReportRules.PeriodOf(query.From, query.To);
        var previousPeriod = query.CompareYear ? HostReportRules.PreviousYearPeriodOf(query.From, query.To) : null;

        var rows = new List<OwnerPortalRow>();
        foreach (var org in owned.GroupBy(o => o.OrgId))
        {
            var weights = org.ToDictionary(o => o.PropertyId, o => o.Weight);
            var report = await host.GetPerformanceAsync(
                new HostScope(org.Key),
                new HostPerformanceQuery(
                    query.From, query.To, null, HostPerformanceGroupBy.Property, query.CompareYear, weights.Keys.ToList()),
                cancellationToken);

            foreach (var row in report.Rows)
            {
                if (row.PropertyId is not { } propertyId || !weights.TryGetValue(propertyId, out var weight))
                    continue;

                rows.Add(new OwnerPortalRow(
                    row.Key,
                    propertyId,
                    row.Label,
                    OwnerPerformanceFigures.Of(row.Figures, weight),
                    row.PreviousYear is null ? null : OwnerPerformanceFigures.Of(row.PreviousYear, weight)));
            }
        }

        rows = rows
            .OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
        var previous = rows.Where(r => r.PreviousYear is not null).Select(r => r.PreviousYear!).ToList();
        var total = new OwnerPortalRow(
            HostPerformanceRules.TotalKey,
            null,
            HostPerformanceRules.TotalKey,
            rows.Aggregate(OwnerPerformanceFigures.Zero, (sum, row) => sum.Plus(row.Figures)),
            previous.Count == 0 ? null : previous.Aggregate(OwnerPerformanceFigures.Zero, (sum, figures) => sum.Plus(figures)));

        return new OwnerPortalOverview(
            period,
            previousPeriod,
            query.GroupBy == HostPerformanceGroupBy.Property ? rows : [],
            total);
    }

    /// <summary>
    /// The owner's trend (NF-96): the Home's trend of each org, narrowed to the owner's properties, each property line weighted by the
    /// owner's share. The buckets are the host's (<see cref="HostReportRules.TrendBucketsOf"/>); all the properties together are the sum of
    /// the weighted lines.
    /// </summary>
    public async Task<OwnerPortalTrend> GetOwnerTrendAsync(
        Guid ownerUserId,
        OwnerOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var host = HostDashboard();
        var owned = await OwnedPropertiesAsync(ownerUserId, query, cancellationToken);
        var period = HostReportRules.PeriodOf(query.From, query.To);
        var previousPeriod = query.CompareYear ? HostReportRules.PreviousYearPeriodOf(query.From, query.To) : null;
        var (unit, buckets) = HostReportRules.TrendBucketsOf(period, HostTrendBucketUnit.Auto, query.CompareYear);

        // One weighted line for each property of the owner, in every org: the host's trend cut by property, none of it folded.
        var lines = new List<OwnerPortalSeries>();
        foreach (var org in owned.GroupBy(o => o.OrgId))
        {
            var weights = org.ToDictionary(o => o.PropertyId, o => o.Weight);
            var trend = await host.GetTrendAsync(
                new HostScope(org.Key),
                new HostTrendQuery(
                    query.From,
                    query.To,
                    null,
                    HostPerformanceGroupBy.Property,
                    query.CompareYear,
                    weights.Keys.ToList(),
                    HostTrendBucketUnit.Auto,
                    int.MaxValue),
                cancellationToken);

            foreach (var line in trend.Series)
            {
                if (line.PropertyId is not { } propertyId || !weights.TryGetValue(propertyId, out var weight))
                    continue;

                lines.Add(new OwnerPortalSeries(
                    line.Key,
                    line.Label,
                    propertyId,
                    line.Points.Select(p => OwnerPerformanceFigures.Of(p.Figures, weight)).ToList(),
                    line.PreviousYear.Select(p => p is null ? null : OwnerPerformanceFigures.Of(p.Figures, weight)).ToList()));
            }
        }

        var series = query.GroupBy == HostPerformanceGroupBy.Property
            ? FoldLines(lines)
            : [Together(HostPerformanceRules.TotalKey, lines, buckets.Count, query.CompareYear)];
        return new OwnerPortalTrend(period, previousPeriod, unit, buckets, series);
    }

    // The ownerships of the owner that count for the days of the query, one entry for each property with the part held. An owner sees
    // only its own ownerships (ADR-011-R2): the ones in force today that had started by the last day and had not ended before the first.
    private async Task<List<OwnedProperty>> OwnedPropertiesAsync(
        Guid ownerUserId,
        OwnerOverviewQuery query,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ownerships = await Ownerships()
            .Where(po => po.OwnerUserId == ownerUserId
                         && po.OwnershipStart <= now
                         && (po.OwnershipEnd == null || po.OwnershipEnd >= now))
            .Select(po => new { po.OrgId, po.PropertyId, po.SharePercent, po.OwnershipStart, po.OwnershipEnd })
            .ToListAsync(cancellationToken);

        // Several rows of one property are parts of one whole; the owner never holds more than all of it.
        var owned = ownerships
            .Where(po => RomeCalendar.DateInRome(po.OwnershipStart) <= query.To
                         && (po.OwnershipEnd is not { } end || RomeCalendar.DateInRome(end) >= query.From))
            .GroupBy(po => (po.OrgId, po.PropertyId))
            .Select(group => new OwnedProperty(group.Key.OrgId, group.Key.PropertyId, Math.Min(1m, group.Sum(po => po.SharePercent) / 100m)))
            .Where(o => o.Weight > 0m)
            .ToList();

        if (query.PropertyId is not { } asked)
            return owned;

        // One 404 for a property that is missing, another owner's, or no longer (or not yet) the owner's.
        owned = owned.Where(o => o.PropertyId == asked).ToList();
        if (owned.Count == 0)
            throw new NotFoundException($"Property {asked} not found") { MessageKey = "PropertyNotFound" };

        return owned;
    }

    /// <summary>
    /// The ownerships of every org: the caller of the owner portal has no org of its own (the tenant filter of its request is fail-closed and would answer
    /// nothing), so what an owner reaches is named by its own <c>OwnerUserId</c> in each predicate.
    /// </summary>
    private IQueryable<PropertyOwnership> Ownerships() =>
        db.Set<PropertyOwnership>().IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);

    private IHostDashboardService HostDashboard() =>
        dashboard ?? throw new InvalidOperationException("The owner portal numbers need the Home's service, which the dependency injection provides.");

    // The lines of the properties, the ones with most revenue first; from MaxTrendSeries + 1 on, the 25th line is the sum of the rest.
    private static List<OwnerPortalSeries> FoldLines(List<OwnerPortalSeries> lines)
    {
        var ordered = lines
            .OrderByDescending(line => line.Points.Sum(p => p.Revenue))
            .ThenBy(line => line.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.Key, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count <= HostPerformanceRules.MaxTrendSeries)
            return ordered;

        var kept = ordered.Take(HostPerformanceRules.MaxTrendSeries - 1).ToList();
        var rest = ordered.Skip(HostPerformanceRules.MaxTrendSeries - 1).ToList();
        kept.Add(Together(HostPerformanceRules.OtherSeriesKey, rest, rest[0].Points.Count, rest[0].PreviousYear.Count > 0));
        return kept;
    }

    // Several lines as one: the points added bucket by bucket; a bucket of the year before is empty only when it is for every line.
    private static OwnerPortalSeries Together(string key, IReadOnlyList<OwnerPortalSeries> lines, int buckets, bool compared)
    {
        var points = new List<OwnerPerformanceFigures>(buckets);
        var before = new List<OwnerPerformanceFigures?>();
        for (var index = 0; index < buckets; index++)
        {
            points.Add(lines.Aggregate(OwnerPerformanceFigures.Zero, (sum, line) => sum.Plus(line.Points[index])));
            if (!compared)
                continue;

            var kept = lines.Select(line => line.PreviousYear[index]).Where(p => p is not null).Select(p => p!).ToList();
            before.Add(kept.Count == 0 ? null : kept.Aggregate(OwnerPerformanceFigures.Zero, (sum, figures) => sum.Plus(figures)));
        }

        return new OwnerPortalSeries(key, key, null, points, before);
    }
}
