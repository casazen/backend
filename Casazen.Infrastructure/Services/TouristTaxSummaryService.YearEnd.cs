using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed partial class TouristTaxSummaryService
{
    public async Task<TouristTaxDeclaration> GetDeclarationAsync(HostScope scope, int year, bool withExemptionReasons, CancellationToken cancellationToken = default)
    {
        RequireYearEnd(scope, year);

        var built = await BuildSummaryAsync(scope, FiscalReportPeriod.WholeYear(year), cancellationToken);

        var stays = built.Report.Stays.Where(s => !built.Reach.ForeignPropertyIds.Contains(s.PropertyId)).ToList();
        var facts = await StayFactsAsync(scope.OrgId, [.. stays.Select(s => s.BookingId)], cancellationToken);

        var comuni = built.Summary.Comuni
            .Select(summary =>
            {
                var key = TouristTaxSummaryRules.ComuneKeyOf(summary.Comune);
                var ofComune = stays.Where(s => TouristTaxSummaryRules.ComuneKeyOf(s.Comune) == key).ToList();
                return new TouristTaxDeclarationComune(summary, ofComune.Count(s => s.Amount > 0), NoAmountOf(ofComune, facts), ExemptionsOf(ofComune, facts, withExemptionReasons));
            })
            .ToList();
        return new TouristTaxDeclaration(year, comuni, built.Summary.Total);
    }

    public async Task<TouristTaxAgentAccount> GetAgentAccountAsync(
        HostScope scope,
        int year,
        TouristTaxAccountPeriod by,
        CancellationToken cancellationToken = default)
    {
        RequireYearEnd(scope, year);

        var rows = new List<TouristTaxAgentAccountRow>();
        foreach (var (label, from, to) in TouristTaxYearEndRules.PeriodsOf(year, by))
        {
            var summary = (await BuildSummaryAsync(scope, new FiscalReportPeriod(from, to), cancellationToken)).Summary;
            rows.AddRange(summary.Comuni.Select(c => AccountRow(c, label, from, to, isYear: false)));
        }

        var whole = (await BuildSummaryAsync(scope, FiscalReportPeriod.WholeYear(year), cancellationToken)).Summary;
        var first = new DateOnly(year, 1, 1);
        var last = new DateOnly(year, 12, 31);
        rows.AddRange(whole.Comuni.Select(c => AccountRow(c, year.ToString("0000", System.Globalization.CultureInfo.InvariantCulture), first, last, isYear: true)));

        return new TouristTaxAgentAccount(
            year,
            by,
            [
                .. rows
                    .OrderBy(r => TouristTaxSummaryRules.ComuneKeyOf(r.Comune), StringComparer.Ordinal)
                    .ThenBy(r => r.IsYear)
                    .ThenBy(r => r.From),
            ],
            [
                .. whole.Comuni
                    .SelectMany(c => c.Remittances ?? [])
                    .OrderBy(r => TouristTaxSummaryRules.ComuneKeyOf(r.Comune), StringComparer.Ordinal)
                    .ThenBy(r => r.PeriodFrom)
                    .ThenBy(r => r.PeriodTo)
                    .ThenBy(r => r.PaidOn)
                    .ThenBy(r => r.Id),
            ],
            whole.Total);
    }

    public async Task<TicinoYearlyCount> GetTicinoYearlyCountAsync(HostScope scope, int year, CancellationToken cancellationToken = default)
    {
        RequireYearEnd(scope, year);

        var orgId = scope.OrgId;
        var due = TouristTaxYearEndRules.TicinoCountDue(year);

        // The properties in Ticino, the deleted ones too (their stays are history), placed by the one resolver of the place of a Swiss property (E29/F01/US01/T02): the municipality stored on
        // each, or the one its postal code and town give. A property in Switzerland whose canton is not known (no place, or a postal code that leaves several municipalities open) may be in
        // Ticino: it is counted too and flagged, never dropped in silence.
        var swiss = await db.Properties.AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.OrgId == orgId && p.Country == "CH")
            .Select(p => new { p.Id, p.Name, p.Address, p.City, p.PostalCode, p.SwissMunicipalityCode, p.Currency, p.IsDeleted })
            .ToListAsync(cancellationToken);
        var places = await (swissPlaces ?? new PropertySwissPlaceResolver(db)).ResolveManyAsync(
            [.. swiss.Select(p => new SwissPlaceLookup(p.Id, "CH", p.SwissMunicipalityCode, p.PostalCode, p.City))], cancellationToken);
        var properties = swiss
            .Select(p => (Property: p, Place: places.GetValueOrDefault(p.Id)))
            .Where(p => InTheTicinoCount(p.Place))
            .ToList();
        if (properties.Count == 0)
            return new TicinoYearlyCount(year, due, []);

        var propertyIds = properties.Select(p => p.Property.Id).ToList();
        var firstNight = new DateOnly(year, 1, 1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var nextYear = new DateOnly(year + 1, 1, 1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var stays = await db.Bookings.AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(b => b.OrgId == orgId
                && propertyIds.Contains(b.PropertyId)
                && b.CheckInDate >= firstNight
                && b.CheckInDate < nextYear
                && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                && b.NoShowAt == null)
            .Select(b => new TicinoStay(b.Id, b.PropertyId, b.CheckInDate, b.CheckOutDate))
            .ToListAsync(cancellationToken);

        var bookingIds = stays.Select(s => s.BookingId).ToList();
        var documents = new List<TicinoDocument>();
        if (bookingIds.Count > 0)
        {
            documents = await db.FiscalDocuments.AsNoTracking()
                .Where(d => d.OrgId == orgId
                    && d.Purpose == FiscalDocumentPurpose.GuestStay
                    && d.Status == FiscalDocumentStatus.Issued
                    && d.Kind != FiscalDocumentKind.Proforma
                    && d.BookingId != null
                    && bookingIds.Contains(d.BookingId.Value))
                .Select(d => new TicinoDocument(
                    d.BookingId!.Value,
                    d.Kind,
                    d.Currency,
                    d.Lines.Where(l => l.LineType == FiscalLineType.TouristTax).Sum(l => (decimal?)l.TotalPrice) ?? 0m))
                .ToListAsync(cancellationToken);
        }

        var documentsOf = documents.ToLookup(d => d.BookingId);
        var counts = new List<TicinoPropertyCount>();
        foreach (var (property, place) in properties)
        {
            var count = CountOf(property.Id, stays, documentsOf, property.Currency);
            if (count.Stays == 0 && property.IsDeleted)
                continue;

            var known = place is { IsKnown: true } ? place : null;
            counts.Add(new TicinoPropertyCount(
                property.Id,
                property.Name,
                property.Address,
                property.City,
                known?.MunicipalityCode ?? property.SwissMunicipalityCode,
                property.Currency,
                count.Stays,
                count.Nights,
                count.TaxedStays,
                count.TaxedNights,
                count.UntaxedStays,
                count.UntaxedNights,
                count.StaysWithoutDocument,
                count.NightsWithoutDocument,
                count.TouristTax,
                CantonUnknown: known is null,
                MunicipalityDerived: known?.Source == SwissPlaceSource.Derived));
        }

        return new TicinoYearlyCount(year, due, [.. counts.OrderBy(c => c.PropertyName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.PropertyId)]);
    }

    private static bool InTheTicinoCount(SwissPlace? place) => place is not { IsKnown: true } || place.IsInTicino;

    /// <summary>
    /// The count of one property: a stay is taxed when the tourist tax lines of its issued documents add up to more than nothing (a credit note takes its amount away), untaxed when it has an issued
    /// document without such a tax (the portal collects it, the guest is exempt, no rule), and without document when none was issued yet (never counted as untaxed). The nights of a stay are its
    /// check-out less its check-in; a stay is counted whole in the year of its check-in. Only the documents in the currency of the property count for the amount.
    /// </summary>
    private static TicinoCount CountOf(Guid propertyId, IReadOnlyList<TicinoStay> stays, ILookup<Guid, TicinoDocument> documentsOf, string currency)
    {
        var count = new TicinoCount();
        foreach (var stay in stays.Where(s => s.PropertyId == propertyId))
        {
            var nights = Math.Max(0, (stay.CheckOut.Date - stay.CheckIn.Date).Days);
            count.Stays++;
            count.Nights += nights;

            var documents = documentsOf[stay.BookingId].ToList();
            if (documents.Count == 0)
            {
                count.StaysWithoutDocument++;
                count.NightsWithoutDocument += nights;
                continue;
            }

            var tax = documents.Where(d => string.Equals(d.Currency, currency, StringComparison.Ordinal)).Sum(d => (d.Kind == FiscalDocumentKind.CreditNote ? -1m : 1m) * d.TouristTax);
            count.TouristTax += tax;
            if (tax > 0m)
            {
                count.TaxedStays++;
                count.TaxedNights += nights;
            }
            else
            {
                count.UntaxedStays++;
                count.UntaxedNights += nights;
            }
        }

        return count;
    }

    private async Task<Dictionary<Guid, TouristTaxStayFacts>> StayFactsAsync(Guid orgId, IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken)
    {
        var facts = new Dictionary<Guid, TouristTaxStayFacts>();
        if (bookingIds.Count == 0)
            return facts;

        var records = await db.StayTouristTaxes.AsNoTracking()
            .Where(t => t.OrgId == orgId && bookingIds.Contains(t.BookingId))
            .Select(t => new { t.Id, t.BookingId, Confirmed = t.ConfirmedAt != null, t.Status, t.Collector, t.DeclaredCollector })
            .ToListAsync(cancellationToken);

        // The guests of the confirmed records only: a position of the stay with the kind of its exemption and the reason code (encrypted at rest, art. 9 GDPR), counted and never listed.
        var confirmed = records.Where(r => r.Confirmed).Select(r => r.Id).ToList();
        var guests = new List<GuestRow>();
        if (confirmed.Count > 0)
        {
            guests = await db.Set<StayTouristTaxGuest>().AsNoTracking()
                .Where(g => confirmed.Contains(g.StayTouristTaxId))
                .Select(g => new GuestRow(g.StayTouristTaxId, g.ExemptionKind, g.ExemptionCode))
                .ToListAsync(cancellationToken);
        }

        var guestsOf = guests.ToLookup(g => g.RecordId);
        foreach (var record in records)
        {
            facts[record.BookingId] = new TouristTaxStayFacts(
                record.Confirmed,
                record.Status,
                record.Collector,
                record.DeclaredCollector,
                [.. guestsOf[record.Id].Select(g => new TouristTaxGuestFact(g.Kind, g.Code))]);
        }

        return facts;
    }

    private static List<TouristTaxReasonCount> NoAmountOf(IReadOnlyList<TouristTaxStayLine> stays, IReadOnlyDictionary<Guid, TouristTaxStayFacts> facts) =>
        stays
            .Where(s => s.Amount <= 0)
            .GroupBy(s => TouristTaxYearEndRules.NoAmountReasonOf(facts.GetValueOrDefault(s.BookingId)), StringComparer.Ordinal)
            .Select(g => new TouristTaxReasonCount(g.Key, g.Count(), g.Sum(s => s.Guests), g.Sum(s => s.Guests * s.Nights)))
            .OrderBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();

    private static List<TouristTaxReasonCount> ExemptionsOf(IReadOnlyList<TouristTaxStayLine> stays, IReadOnlyDictionary<Guid, TouristTaxStayFacts> facts, bool withReasons)
    {
        var exempt = new List<(string Reason, Guid BookingId, int Nights)>();
        foreach (var stay in stays)
        {
            if (!facts.TryGetValue(stay.BookingId, out var stayFacts) || !stayFacts.Confirmed)
                continue;

            foreach (var guest in stayFacts.Guests)
            {
                if (TouristTaxYearEndRules.ExemptionReasonOf(guest, withReasons) is { } reason)
                    exempt.Add((reason, stay.BookingId, stay.Nights));
            }
        }

        return exempt
            .GroupBy(e => e.Reason, StringComparer.Ordinal)
            .Select(g => new TouristTaxReasonCount(g.Key, g.Select(e => e.BookingId).Distinct().Count(), g.Count(), g.Sum(e => e.Nights)))
            .OrderBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();
    }

    private static TouristTaxAgentAccountRow AccountRow(ComuneTaxSummary comune, string period, DateOnly from, DateOnly to, bool isYear) =>
        new(comune.Comune, period, from, to, isYear, comune.Stays, comune.Nights, comune.Guests, comune.Owed, comune.StaysWithoutAmount, comune.Paid ?? 0m, comune.Balance ?? 0m);

    private static void RequireYearEnd(HostScope scope, int year)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.IsOrgWide)
            throw new DomainForbiddenException(TouristTaxErrorCodes.YearEndNeedsWholeOrg, "TouristTaxYearEndNeedsWholeOrg");

        if (year is < 2000 or > 2100)
            throw new FiscalValidationException("fiscal_report_period_invalid", "FiscalReportPeriodInvalid");
    }

    private sealed record GuestRow(Guid RecordId, TouristTaxExemptionKind Kind, string? Code);

    private sealed record TicinoStay(Guid BookingId, Guid PropertyId, DateTime CheckIn, DateTime CheckOut);

    private sealed record TicinoDocument(Guid BookingId, FiscalDocumentKind Kind, string Currency, decimal TouristTax);

    private sealed class TicinoCount
    {
        public int Stays { get; set; }

        public int Nights { get; set; }

        public int TaxedStays { get; set; }

        public int TaxedNights { get; set; }

        public int UntaxedStays { get; set; }

        public int UntaxedNights { get; set; }

        public int StaysWithoutDocument { get; set; }

        public int NightsWithoutDocument { get; set; }

        public decimal TouristTax { get; set; }
    }
}
