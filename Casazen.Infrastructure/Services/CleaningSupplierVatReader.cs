using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The VAT of the company stated for the cleaning requests of an organization (FIX/BE-CLEANING-VAT): the one read the posting of the month, the close, the owner invoice
/// and the question share, so they never disagree. The request's own answer first; else what the organization remembers for the company, «Sì» with the standard rate of the
/// country of the cleaning on its day (<see cref="VatRates"/>: a day the table has no rate for leaves the request without a stated VAT, never with a rate of its own); else
/// nothing.
/// </summary>
/// <remarks>
/// Only the organization's cleaning requests count (the requests are not tenant-filtered, two parties: the org is the predicate, and an id of another org is not in the
/// answer); a staff activity, a showcase request or a request of another category is not either. The country of a cleaning is the one of its property, deleted ones too.
/// Three reads at most, none when nothing is asked: the requests, then the properties and the remembered answers they need.
/// </remarks>
internal static class CleaningSupplierVatReader
{
    /// <summary>The day of the cleaning, whose rate «Sì» uses: the day it was completed (the payment, then the last change, for a row that has none), on the Europe/Rome calendar.</summary>
    public static DateOnly DayOf(DateTime? completedAt, DateTime? paidAt, DateTime updatedAt) => RomeCalendar.DateInRome(completedAt ?? paidAt ?? updatedAt);

    /// <summary>
    /// The rule of the owner invoices of the organization on <paramref name="day"/> (the last day of a month): its own issuer profile and the catalog, as the owner invoice
    /// resolves it; <c>null</c> without a profile, without a rule in the catalog or without the resolver. Whether the question is asked depends on it
    /// (<see cref="CleaningVatRules.IsAskedUnder"/>).
    /// </summary>
    public static async Task<FiscalTaxRule?> OwnerInvoiceRuleAsync(AppDbContext db, IFiscalTaxRuleResolver? taxRules, Guid orgId, DateOnly day, CancellationToken ct)
    {
        if (taxRules is null)
            return null;

        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().OfOrganization(orgId).FirstOrDefaultAsync(ct);
        return profile is null
            ? null
            : taxRules.TryResolve(profile.CountryCode, profile.DocumentRegime, profile.StayVatTreatment, profile.CleaningFeeTreatment, day);
    }

    /// <summary>The country of each of the properties of the organization (deleted ones too: the cleaning stays the owner's), upper case; an empty list is an empty answer.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> CountriesAsync(AppDbContext db, Guid orgId, IReadOnlyCollection<Guid> propertyIds, CancellationToken ct)
    {
        if (propertyIds.Count == 0)
            return new Dictionary<Guid, string>();

        var ids = propertyIds.Distinct().ToList();
        return await db.Properties
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Country })
            .ToDictionaryAsync(p => p.Id, p => (p.Country ?? string.Empty).Trim().ToUpperInvariant(), ct);
    }

    /// <summary>The stated VAT of each of <paramref name="requestIds"/> that has one; an empty list is an empty answer.</summary>
    public static async Task<IReadOnlyDictionary<Guid, StatedCleaningVat>> ReadAsync(
        AppDbContext db,
        Guid orgId,
        IReadOnlyCollection<Guid> requestIds,
        CancellationToken ct)
    {
        var stated = new Dictionary<Guid, StatedCleaningVat>();
        if (requestIds.Count == 0)
            return stated;

        var ids = requestIds.Distinct().ToList();
        var requests = await db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.OrgId == orgId
                        && ids.Contains(r.Id)
                        && r.RentalContext != ServiceRequestRentalContext.Showcase
                        && r.PropertyId != null
                        && r.Category == ServiceCategories.Cleaning)
            .Select(r => new
            {
                r.Id,
                PropertyId = r.PropertyId!.Value,
                r.SupplierOrgId,
                r.SupplierVat,
                r.SupplierVatRatePercent,
                r.CompletedAt,
                r.PaidAt,
                r.UpdatedAt,
            })
            .ToListAsync(ct);
        if (requests.Count == 0)
            return stated;

        var propertyIds = requests.Select(r => r.PropertyId).Distinct().ToList();
        var countries = await db.Properties
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && propertyIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Country })
            .ToDictionaryAsync(p => p.Id, p => (p.Country ?? string.Empty).Trim().ToUpperInvariant(), ct);

        var companies = requests.Where(r => r.SupplierVat is null).Select(r => r.SupplierOrgId).Distinct().ToList();
        var remembered = companies.Count == 0
            ? new Dictionary<Guid, bool>()
            : await db.SupplierVatDefaults
                .AsNoTracking()
                .Where(d => d.OrgId == orgId && companies.Contains(d.SupplierOrgId))
                .ToDictionaryAsync(d => d.SupplierOrgId, d => d.AppliesVat, ct);

        foreach (var request in requests)
        {
            var country = countries.GetValueOrDefault(request.PropertyId) ?? string.Empty;
            if (request.SupplierVat is { } own)
            {
                stated[request.Id] = new StatedCleaningVat(own, own == CleaningSupplierVat.Applied ? request.SupplierVatRatePercent : null, country, FromCompany: false);
                continue;
            }

            if (!remembered.TryGetValue(request.SupplierOrgId, out var appliesVat))
                continue;

            if (!appliesVat)
            {
                stated[request.Id] = new StatedCleaningVat(CleaningSupplierVat.NotApplied, null, country, FromCompany: true);
                continue;
            }

            // «Sì» for the company: the standard rate of the country of this cleaning on its day, when the table has one; else the request stays to be answered.
            if (VatRates.Find(country, VatRateKind.Standard, DayOf(request.CompletedAt, request.PaidAt, request.UpdatedAt)) is { } rate)
                stated[request.Id] = new StatedCleaningVat(CleaningSupplierVat.Applied, rate.Percent, country, FromCompany: true);
        }

        return stated;
    }
}
