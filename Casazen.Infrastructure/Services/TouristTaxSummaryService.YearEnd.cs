using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The tourist tax at year end (E18/F01/US02/T03, NF-117, acceptance steps C9 and D3 of the complete flow): the declaration data per comune, the account of the agent and the yearly count of nights of
/// Ticino. They are made of what the tourist tax summary already gives (<see cref="BuildSummaryAsync"/>: owed from the stays the engine recorded, remitted from the payments with their days and
/// references, the difference to the cent) and of what the stays and the documents store (the reason a stay has no amount, the guests' exemptions by reason, the tourist tax lines of the issued documents
/// of the stays of Ticino): nothing is recomputed, and nothing is filed by CasaZen. All three need a scope that reaches the whole org (a declaration of some of the properties would be false, and the
/// payments belong to the org): 403 before anything is read.
/// </summary>
public sealed partial class TouristTaxSummaryService
{
    public async Task<TouristTaxDeclaration> GetDeclarationAsync(HostScope scope, int year, bool withExemptionReasons, CancellationToken cancellationToken = default)
    {
        RequireYearEnd(scope, year);

        var built = await BuildSummaryAsync(scope, FiscalReportPeriod.WholeYear(year), cancellationToken);

        // The stays of the report in Italy (the summary leaves out a house abroad), with what each stores of its tourist tax.
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

        // The summary of each month or quarter: what each comune is owed in it and what was remitted for it (a payment counts in the period its declared period lies inside).
        var rows = new List<TouristTaxAgentAccountRow>();
        foreach (var (label, from, to) in TouristTaxYearEndRules.PeriodsOf(year, by))
        {
            var summary = (await BuildSummaryAsync(scope, new FiscalReportPeriod(from, to), cancellationToken)).Summary;
            rows.AddRange(summary.Comuni.Select(c => AccountRow(c, label, from, to, isYear: false)));
        }

        // The line of the year of each comune: every payment of the year, so a payment that spans two periods is still in the account.
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

        // The properties in Ticino, the deleted ones too (their stays are history): the canton of the municipality of each, as the accountant package tells the Swiss tourist tax. A property in
        // Switzerland whose canton is not known (an empty canton: no municipality on it, or one the registry lacks) may be in Ticino: it is counted too and flagged, never dropped in silence.
        var swiss = await db.Properties.AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter])
            .Where(p => p.OrgId == orgId && p.Country == "CH")
            .Select(p => new { p.Id, p.Name, p.Address, p.City, p.SwissMunicipalityCode, p.Currency, p.IsDeleted })
            .ToListAsync(cancellationToken);
        var cantons = await AccountantPackageFacts.SwissCantonsAsync(db, orgId, [.. swiss.Select(p => p.Id)], cancellationToken);
        var properties = swiss
            .Where(p => cantons.TryGetValue(p.Id, out var canton) && (canton.Length == 0 || string.Equals(canton, TouristTaxYearEndRules.TicinoCanton, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (properties.Count == 0)
            return new TicinoYearlyCount(year, due, []);

        // The stays with check-in in the year that happened (the one definition of an active stay, AlloggiatiStatusRules: confirmed, checked in or checked out, never a request, a cancelled
        // booking or a no-show), and the documents issued for them.
        var propertyIds = properties.Select(p => p.Id).ToList();
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
        foreach (var property in properties)
        {
            var count = CountOf(property.Id, stays, documentsOf, property.Currency);
            if (count.Stays == 0 && property.IsDeleted)
                continue;

            counts.Add(new TicinoPropertyCount(
                property.Id,
                property.Name,
                property.Address,
                property.City,
                property.SwissMunicipalityCode,
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
                cantons[property.Id].Length == 0));
        }

        return new TicinoYearlyCount(year, due, [.. counts.OrderBy(c => c.PropertyName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.PropertyId)]);
    }

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

    /// <summary>What a stay stores of its tourist tax (<see cref="StayTouristTax"/>) and, once confirmed, of its guests: the one record of a booking, and its guest lines.</summary>
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

    /// <summary>The stays with no amount of a comune, by the reason they have none (<see cref="TouristTaxYearEndRules.NoAmountReasonOf"/>); their sum is the comune's <c>StaysWithoutAmount</c>.</summary>
    private static List<TouristTaxReasonCount> NoAmountOf(IReadOnlyList<TouristTaxStayLine> stays, IReadOnlyDictionary<Guid, TouristTaxStayFacts> facts) =>
        stays
            .Where(s => s.Amount <= 0)
            .GroupBy(s => TouristTaxYearEndRules.NoAmountReasonOf(facts.GetValueOrDefault(s.BookingId)), StringComparer.Ordinal)
            .Select(g => new TouristTaxReasonCount(g.Key, g.Count(), g.Sum(s => s.Guests), g.Sum(s => s.Guests * s.Nights)))
            .OrderBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The exempt guests of the confirmed stays of a comune by reason, with the stays they are in and the nights they spent: a count, never a guest, a name or a booking. A stay may have taxed
    /// and exempt guests, so the stays with an amount are counted here too.
    /// </summary>
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

    /// <summary>A comune of a period of the account of the agent: the figures of the summary (the payments are shown to a scope of the whole org, as it always is here).</summary>
    private static TouristTaxAgentAccountRow AccountRow(ComuneTaxSummary comune, string period, DateOnly from, DateOnly to, bool isYear) =>
        new(comune.Comune, period, from, to, isYear, comune.Stays, comune.Nights, comune.Guests, comune.Owed, comune.StaysWithoutAmount, comune.Paid ?? 0m, comune.Balance ?? 0m);

    /// <summary>
    /// The year-end exports need a scope that reaches the whole org (403 <see cref="TouristTaxErrorCodes.YearEndNeedsWholeOrg"/>, before anything is read) and a year the reports support (400
    /// <c>fiscal_report_period_invalid</c>).
    /// </summary>
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
