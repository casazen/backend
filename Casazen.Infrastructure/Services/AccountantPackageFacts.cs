using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>A line of an issued fiscal document as the accountant package reads it: its type, its taxable amount, its VAT rate and its stored VAT.</summary>
internal sealed record PackageDocumentLine(int LineNumber, FiscalLineType LineType, decimal TotalPrice, decimal VatRatePercent, string? VatNatureCode, decimal? VatAmount);

/// <summary>
/// An issued fiscal document as the accountant package and the agency summary read it (E15/F01/US01/T01): what the document stores, to the cent, with its lines and
/// without its XML, its recipient's fiscal data or its address. The same read for both, so the register of the package and the figures of the summary are made of
/// the same documents.
/// </summary>
internal sealed record PackageDocument(
    Guid Id,
    FiscalDocumentPurpose Purpose,
    FiscalDocumentKind Kind,
    FiscalDocumentSource Source,
    string? Number,
    DateOnly IssueDate,
    string Currency,
    string? IssuerSnapshot,
    string? RecipientName,
    Guid? RecipientOwnerId,
    Guid? PropertyId,
    FiscalPaymentChannel PaymentChannel,
    decimal TaxableBase,
    decimal VatAmount,
    decimal StampDuty,
    decimal Total,
    decimal? VatRatePercent,
    string? VatNatureCode,
    int? PeriodYear,
    int? PeriodMonth,
    FiscalDocumentKind? CreditedKind,
    bool HasXml,
    List<PackageDocumentLine> Lines)
{
    /// <summary>The sign a document counts with: a credit note takes away what it credits (it stores positive amounts).</summary>
    public decimal Sign => Kind == FiscalDocumentKind.CreditNote ? -1m : 1m;
}

/// <summary>One VAT rate of a document: the taxable amount and the stored VAT, as the document states them (a credit note still positive). A rate that is not stated is <c>null</c>.</summary>
internal sealed record PackageVatRow(decimal? RatePercent, string? NatureCode, decimal Taxable, decimal Vat);

/// <summary>
/// What the accountant package and the agency summary read from the fiscal documents and the owner ledger, in one place (E15/F01/US01/T01): the issued documents of a
/// period with their lines, the issuer a document was issued with, its VAT for each rate, the names of the owners, the canton of a Swiss property and the portal
/// commissions of the accounts the agency holds.
/// </summary>
internal static class AccountantPackageFacts
{
    /// <summary>The issued documents of <paramref name="purpose"/> of the org in <paramref name="period"/> (the day of the issue), narrowed in SQL to the reach of <paramref name="scope"/>.</summary>
    /// <remarks>
    /// The documents of a stay reach a scope that reaches the property of their booking (the narrowing of the list of the documents,
    /// <c>FiscalDocumentService.Matching</c>); the invoices to the owners have no booking and belong to no property, so they reach the whole org only: a scope that
    /// does not reach it reads none. A proforma is not a document and a draft or a discarded one is not issued.
    /// </remarks>
    public static IQueryable<FiscalDocument> IssuedDocuments(AppDbContext db, HostScope scope, AccountantPackagePeriod period, FiscalDocumentPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(period);

        var orgId = scope.OrgId;
        var from = period.From;
        var to = period.To;
        var query = db.FiscalDocuments.AsNoTracking()
            .Where(d => d.OrgId == orgId && d.Purpose == purpose && d.Status == FiscalDocumentStatus.Issued && d.Kind != FiscalDocumentKind.Proforma
                        && d.IssueDate != null && d.IssueDate >= from && d.IssueDate <= to);
        if (scope.IsOrgWide)
            return query;

        if (purpose != FiscalDocumentPurpose.GuestStay)
            return query.Where(_ => false);

        var bookings = db.Bookings.IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter]).Where(b => b.OrgId == orgId).InScope(scope);
        return query.Where(d => d.BookingId != null && bookings.Any(b => b.Id == d.BookingId));
    }

    /// <summary>
    /// The documents of <see cref="IssuedDocuments"/> as <see cref="PackageDocument"/>, oldest first (the day of the issue, then the series and the number): the query, not run, so a
    /// test can read how a provider translates it (<see cref="ReadAsync"/> runs it).
    /// </summary>
    public static IQueryable<PackageDocument> Documents(AppDbContext db, HostScope scope, AccountantPackagePeriod period, FiscalDocumentPurpose purpose)
    {
        return IssuedDocuments(db, scope, period, purpose)
            .OrderBy(d => d.IssueDate)
            .ThenBy(d => d.Series)
            .ThenBy(d => d.SequenceNumber)
            .ThenBy(d => d.Id)
            .Select(d => new PackageDocument(
                d.Id,
                d.Purpose,
                d.Kind,
                d.Source,
                d.Number,
                d.IssueDate!.Value,
                d.Currency,
                d.IssuerSnapshot,
                d.RecipientName,
                d.RecipientOwnerId,
                d.PropertyId,
                d.PaymentChannel,
                d.TaxableBase,
                d.VatAmount,
                d.StampDuty,
                d.Total,
                d.VatRatePercent,
                d.VatNatureCode,
                d.PeriodYear,
                d.PeriodMonth,
                db.FiscalDocuments.Where(c => c.OrgId == d.OrgId && c.Id == d.CreditedDocumentId).Select(c => (FiscalDocumentKind?)c.Kind).FirstOrDefault(),
                d.FatturaPaXml != null,
                d.Lines
                    .OrderBy(l => l.LineNumber)
                    .Select(l => new PackageDocumentLine(l.LineNumber, l.LineType, l.TotalPrice, l.VatRatePercent, l.VatNatureCode, l.VatAmount))
                    .ToList()));
    }

    /// <summary>The documents of <see cref="Documents"/>, read.</summary>
    public static async Task<List<PackageDocument>> ReadAsync(
        AppDbContext db, HostScope scope, AccountantPackagePeriod period, FiscalDocumentPurpose purpose, CancellationToken cancellationToken)
    {
        return await Documents(db, scope, period, purpose).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The issuer of the org as its profile and the org say it today: the issuer of a document issued before the snapshot of the issuer existed. The profile of the
    /// org, never the one of an owner (E17/F01/US01/T01: the owners' profiles live in the same table).
    /// </summary>
    public static async Task<FiscalIssuerSnapshot> CurrentIssuerAsync(AppDbContext db, Guid orgId, CancellationToken cancellationToken)
    {
        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().OfOrganization(orgId).FirstOrDefaultAsync(cancellationToken);
        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        return org is null ? new FiscalIssuerSnapshot() : FiscalIssuerSnapshot.From(profile, org);
    }

    /// <summary>The issuer a document was issued with: its snapshot (the issuer as it was at the issue), else <paramref name="fallback"/>.</summary>
    public static AgencyIssuer IssuerOf(string? snapshotJson, FiscalIssuerSnapshot fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        return IssuerOf(FiscalIssuerSnapshot.FromJson(snapshotJson) ?? fallback);
    }

    /// <summary>The identity of an issuer: its country, its tax number (the Swiss UID, else the Italian VAT number, else the fiscal code) and its name.</summary>
    public static AgencyIssuer IssuerOf(FiscalIssuerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var country = string.IsNullOrWhiteSpace(snapshot.CountryCode) ? OrgFiscalIssuerProfile.DefaultCountryCode : snapshot.CountryCode.Trim().ToUpperInvariant();
        var taxId = string.Equals(country, "CH", StringComparison.Ordinal)
            ? snapshot.SwissVatNumber
            : snapshot.VatNumber ?? snapshot.FiscalCode;
        var name = snapshot.DisplayName;
        return new AgencyIssuer(string.Create(CultureInfo.InvariantCulture, $"{country}|{taxId}|{name}"), name, country, taxId, snapshot.Regime);
    }

    /// <summary>
    /// The VAT of a document for each rate, as it stores it: the stored VAT of its lines (<see cref="FiscalDocumentTotals.SummaryOf"/>, never <c>taxable × rate</c> again),
    /// the highest rate first; a document with no lines (one imported from a channel manager) states its own totals, with the rate it gives or none.
    /// </summary>
    public static IReadOnlyList<PackageVatRow> VatOf(PackageDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Lines.Count == 0)
            return [new PackageVatRow(document.VatRatePercent, document.VatNatureCode, document.TaxableBase, document.VatAmount)];

        var lines = document.Lines
            .Select(l => new FiscalDocumentLineAmounts(l.LineNumber, string.Empty, 1m, l.TotalPrice, l.TotalPrice, l.VatRatePercent, l.VatNatureCode, l.LineType, l.VatAmount))
            .ToList();
        return FiscalDocumentTotals.SummaryOf(lines, document.VatAmount)
            .Select(s => new PackageVatRow(s.VatRatePercent, s.NatureCode, s.TaxableAmount, s.Tax))
            .ToList();
    }

    /// <summary>The VAT a line carries: the stored one, else the VAT of its taxable amount at its rate (the computation of the lines written before it was stored).</summary>
    public static decimal VatOfLine(PackageDocumentLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.VatAmount ?? FiscalDocumentTotals.Vat(line.TotalPrice, line.VatRatePercent);
    }

    /// <summary>The quarter of a day, as the Swiss VAT return counts it: <c>2026-Q4</c>.</summary>
    public static string QuarterOf(DateOnly day) => string.Create(CultureInfo.InvariantCulture, $"{day.Year}-Q{((day.Month - 1) / 3) + 1}");

    /// <summary>The month of a day: <c>2026-10</c>.</summary>
    public static string MonthOf(DateOnly day) => day.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>The name the agency keeps for each owner (the legal name, else first and last name); <c>null</c> for an owner whose data was anonymized.</summary>
    public static async Task<Dictionary<Guid, string?>> OwnerNamesAsync(AppDbContext db, Guid orgId, IReadOnlyCollection<Guid> ownerIds, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string?>();
        if (ownerIds.Count == 0)
            return names;

        var owners = await db.PropertyOwners.AsNoTracking()
            .Where(o => o.OrgId == orgId && ownerIds.Contains(o.Id))
            .Select(o => new { o.Id, o.FirstName, o.LastName, o.LegalName, Anonymized = o.AnonymizedAt != null })
            .ToListAsync(cancellationToken);
        foreach (var owner in owners)
        {
            names[owner.Id] = owner.Anonymized ? null
                : !string.IsNullOrWhiteSpace(owner.LegalName) ? owner.LegalName.Trim()
                : $"{owner.FirstName} {owner.LastName}".Trim();
        }

        return names;
    }

    /// <summary>
    /// The canton of each property in Switzerland among <paramref name="propertyIds"/> (the canton of its municipality, an empty text when the property has none): a
    /// property abroad or in Italy is not in the answer, so the tourist tax of a Swiss document is told from the Italian one.
    /// </summary>
    public static async Task<Dictionary<Guid, string>> SwissCantonsAsync(AppDbContext db, Guid orgId, IReadOnlyCollection<Guid> propertyIds, CancellationToken cancellationToken)
    {
        if (propertyIds.Count == 0)
            return new Dictionary<Guid, string>();

        var properties = await db.Properties.AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.OrgId == orgId && propertyIds.Contains(p.Id) && p.Country == "CH")
            .Select(p => new { p.Id, p.SwissMunicipalityCode })
            .ToListAsync(cancellationToken);
        var codes = properties.Where(p => p.SwissMunicipalityCode != null).Select(p => p.SwissMunicipalityCode!).Distinct().ToList();
        var cantons = new Dictionary<string, string>();
        if (codes.Count > 0)
        {
            cantons = await db.SwissMunicipalities.AsNoTracking()
                .Where(m => codes.Contains(m.MunicipalityCode))
                .ToDictionaryAsync(m => m.MunicipalityCode, m => m.CantonCode, cancellationToken);
        }

        return properties.ToDictionary(p => p.Id, p => p.SwissMunicipalityCode is { } code && cantons.TryGetValue(code, out var canton) ? canton : string.Empty);
    }

    /// <summary>
    /// The Swiss tourist tax the documents collected, for each canton and currency (credit notes taken away): the lines of the type tourist tax of the documents of the
    /// properties in Switzerland. What was remitted to the cantonal office is not recorded in CasaZen.
    /// </summary>
    public static async Task<IReadOnlyList<AgencySwissTouristTaxRow>> SwissTouristTaxAsync(
        AppDbContext db, Guid orgId, IReadOnlyList<PackageDocument> guestDocuments, CancellationToken cancellationToken)
    {
        var propertyIds = guestDocuments.Where(d => d.PropertyId is not null).Select(d => d.PropertyId!.Value).Distinct().ToList();
        var cantons = await SwissCantonsAsync(db, orgId, propertyIds, cancellationToken);
        return guestDocuments
            .Where(d => d.PropertyId is { } id && cantons.ContainsKey(id) && d.Lines.Any(l => l.LineType == FiscalLineType.TouristTax))
            .GroupBy(d => (Canton: cantons[d.PropertyId!.Value], d.Currency))
            .Select(g => new AgencySwissTouristTaxRow(
                g.Key.Canton,
                g.Key.Currency,
                g.Count(),
                g.Sum(d => d.Sign * d.Lines.Where(l => l.LineType == FiscalLineType.TouristTax).Sum(l => l.TotalPrice))))
            .OrderBy(r => r.Canton, StringComparer.Ordinal)
            .ThenBy(r => r.Currency, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The commissions the portals kept on the stays of the listings whose account the agency holds (⚖ I10, V2), for each channel and currency: the channel fee lines of the
    /// owner ledger of the statement months of the period, as positive amounts, a reversal and the line it reverses cancelling out. Read through
    /// <see cref="IPortalAccountHolderResolver"/>, the one reader of who holds an account.
    /// </summary>
    public static async Task<IReadOnlyList<AgencyPortalCommissionBorneRow>> PortalCommissionsBorneAsync(
        AppDbContext db, IPortalAccountHolderResolver holders, Guid orgId, AccountantPackagePeriod period, CancellationToken cancellationToken)
    {
        var fromKey = (period.From.Year * 100) + period.From.Month;
        var toKey = (period.To.Year * 100) + period.To.Month;
        var fees = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.EntryType == OwnerLedgerEntryType.ChannelFee && e.PropertyId != null && e.SourceId != null
                        && ((e.PeriodYear * 100) + e.PeriodMonth) >= fromKey && ((e.PeriodYear * 100) + e.PeriodMonth) <= toKey)
            .Select(e => new { PropertyId = e.PropertyId!.Value, StayId = e.SourceId!.Value, e.StatementCurrency, e.Amount })
            .ToListAsync(cancellationToken);
        if (fees.Count == 0)
            return [];

        var stayIds = fees.Select(f => f.StayId).Distinct().ToList();
        var sources = await db.Bookings.AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(b => b.OrgId == orgId && stayIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Source })
            .ToDictionaryAsync(b => b.Id, b => b.Source, cancellationToken);
        var holderMap = await holders.ForOrgAsync(orgId, cancellationToken);
        return fees
            .Where(f => sources.TryGetValue(f.StayId, out var source) && holderMap.Resolve(f.PropertyId, source) == PortalAccountHolder.Agency)
            .GroupBy(f => (Source: sources[f.StayId], Currency: f.StatementCurrency))
            .Select(g => new AgencyPortalCommissionBorneRow(g.Key.Source, g.Key.Currency, -g.Sum(f => f.Amount)))
            .Where(r => r.Amount != 0m)
            .OrderBy(r => r.Source)
            .ThenBy(r => r.Currency, StringComparer.Ordinal)
            .ToList();
    }
}
