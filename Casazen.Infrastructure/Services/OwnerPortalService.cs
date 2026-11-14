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
public sealed class OwnerPortalService(
    AppDbContext db,
    TimeProvider timeProvider,
    IHostDashboardService? dashboard = null,
    IFeatureFlags? featureFlags = null,
    IOwnerPortalAccessResolver? accessResolver = null,
    IOwnerStatementDocuments? statementDocuments = null) : IOwnerPortalService
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
                v.Status == OwnerStatementVersionStatus.Superseded,
                v.SupersededByVersionId is { } later && laterPublished.Contains(later) ? later : null,
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

    /// <summary>
    /// Gets withholding tax summary for the owner (ADR-011-R2).
    /// Returns empty summary for now (withholding feature not yet implemented).
    /// </summary>
    public Task<OwnerPortalWithholdingInfo> GetOwnerWithholdingAsync(
        Guid ownerUserId,
        int? year = null,
        CancellationToken cancellationToken = default)
    {
        var currentYear = year ?? timeProvider.GetUtcNow().Year;

        // Placeholder: withholding feature not yet implemented
        return Task.FromResult(new OwnerPortalWithholdingInfo
        {
            Year = currentYear,
            TotalWithheld = 0m,
            Details = []
        });
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
