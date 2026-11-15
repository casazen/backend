using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The facts of a payout, read from the owner ledger of the version being paid (E18/F01/US01/T01, NF-117, NF-110): the user types none of them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The stays</b> are the owner's revenue lines (<see cref="OwnerLedgerEntryType.BookingRevenue"/>, from a payment or from the booking) of the month of the version
/// and of the months before, posted up to its cut-off, that no confirmed payout of the owner covered yet, added up per stay (a reversal cancels the line it reverses): the
/// owner's share of the gross without the tourist tax (<see cref="OwnerLedgerRules"/> already left it out). A confirmed payout covers the lines its own version is made
/// of and the ones before it (the month of its version and the months before, posted up to its cut-off), whatever its date: a line is decided once, a payout confirmed
/// before E18/F01/US01 included, and the balance of a stay paid in another month (a deposit, then the rest) is decided with the payout that pays it. A stay with nothing
/// left to decide (cancelled, refunded, covered) is no stay of the payout. A reversed payout covers nothing.</item>
/// <item><b>Each stay</b> is judged by <see cref="PayoutFactRules.EvaluateStay"/> on its booking (active, Italian property, nights), the owner (a private natural person,
/// on the registry and on the fiscal profile in force), the checks of E18/F01/US03 (<see cref="IOwnerTaxChecks.TreatmentOfStaysAsync"/>: presumed business, company
/// tenant), how the ledger posted it (payments in CasaZen or recorded by the agency), the portal and who holds its account (<see cref="IPortalAccountHolderResolver"/>),
/// and the withholding of the portal (<see cref="Payment.OtaWithholdingTax"/>, or the portals of ⚖ R5).</item>
/// <item><b>The base</b> is in euro, the currency of the F24: a stay in another currency is converted with the agency's rate of the payout day (or of the 7 days
/// before, <see cref="OwnerAccountingService.RateFor"/>); without one the stay cannot be judged (422 <c>tax_applicability_unresolved</c>, <c>fxRate</c>), never a
/// guess. The rate from the euro to the currency of the version is read too (⚖ R6): the withholding decided in euro is kept from a payout in francs at it.</item>
/// <item><b>Tenant.</b> Every query names the org of the version; nothing is written.</item>
/// </list>
/// </remarks>
public sealed class OwnerPayoutFacts(
    AppDbContext db,
    IOwnerTaxChecks taxChecks,
    IPortalAccountHolderResolver holders,
    IOptions<ShortStayFiscalOptions> fiscalOptions,
    IOptions<OwnerAccountingOptions> accountingOptions)
{
    private static readonly PaymentStatus[] CollectedStatuses = [PaymentStatus.Completed, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded];

    /// <summary>
    /// What the version still owes the owner: its closing balance less the gross of the confirmed payouts of the owner (in its currency) that the version does not
    /// count, those paid in a later month or confirmed after its cut-off. A payout paid in the month of the version or before it and confirmed before the cut-off is
    /// already in the closing balance (its lines are).
    /// </summary>
    public async Task<decimal> UnpaidBalanceAsync(OwnerStatementVersion version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        var nextMonth = new DateOnly(version.PeriodYear, version.PeriodMonth, 1).AddMonths(1);
        var paid = await db.OwnerPayouts.AsNoTracking()
            .Where(p => p.OrgId == version.OrgId && p.PropertyOwnerId == version.PropertyOwnerId && p.Status == OwnerPayoutStatus.Confirmed
                && p.Currency == version.Currency && (p.PaidOn >= nextMonth || p.ConfirmedAt > version.CutoffAt))
            .Select(p => p.GrossAmount)
            .ToListAsync(ct);
        return version.ClosingBalance - paid.Sum();
    }

    /// <summary>
    /// The facts of a payout of <paramref name="version"/> paid on <paramref name="paidOn"/>, judged with <paramref name="rule"/> (the approved rule in force that day;
    /// <c>null</c> in a preview of an owner without one: the requirements of a withholding rule and the configured portals of ⚖ R5) and the fiscal profile in force.
    /// <see cref="PayoutFacts.GrossAmount"/> is left to the caller. 422 <c>tax_applicability_unresolved</c> when a stay cannot be judged (<c>stays</c>: its booking
    /// is gone or its lines are in two currencies; <c>fxRate</c>: no rate of the payout day for a stay of the base in another currency).
    /// </summary>
    public async Task<PayoutFacts> ReadAsync(
        OwnerStatementVersion version,
        DateOnly paidOn,
        OwnerAccountingRuleParameters? rule,
        OwnerFiscalProfile? profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        var orgId = version.OrgId;
        var ownerId = version.PropertyOwnerId;
        var defaults = accountingOptions.Value.ItalianShortLetRule;
        var portals = PayoutFactRules.PortalsOf(rule?.PortalsWithholding ?? defaults.PortalsWithholding);
        var settings = new PayoutFactSettings(
            fiscalOptions.Value.MaxStayNights,
            portals,
            rule?.RequiresAgencyCollection ?? true,
            rule?.RequiresQualifyingShortLet ?? true);

        var rates = await db.OwnerFxRates.AsNoTracking()
            .Where(r => r.OrgId == orgId && r.RateDate >= paidOn.AddDays(-OwnerAccountingService.FxMaxAgeDays) && r.RateDate <= paidOn)
            .ToListAsync(ct);
        var currency = StayCurrency.Normalize(version.Currency);
        var fx = string.Equals(currency, StayCurrency.Euro, StringComparison.Ordinal)
            ? null
            : OwnerAccountingService.RateFor(rates, StayCurrency.Euro, currency, paidOn) is { } quote
                ? new PayoutFx(StayCurrency.Euro, currency, quote.Multiplier, quote.Source, quote.Day)
                : null;

        var stays = await StaysOfAsync(version, ct);
        var facts = new List<PayoutStayFact>(stays.Count);
        if (stays.Count > 0)
        {
            var ids = stays.Keys.ToList();
            var bookings = await db.Bookings.IgnoreQueryFilters([AppDbContext.SoftDeleteQueryFilter]).AsNoTracking()
                .Where(b => b.OrgId == orgId && ids.Contains(b.Id))
                .Select(b => new BookingFacts(
                    b.Id, b.PropertyId, b.Source, b.Status, b.NoShowAt, b.CheckInDate, b.CheckOutDate, b.OtaCompanyCollect, b.TouristTax, b.Property.Country))
                .ToDictionaryAsync(b => b.Id, ct);
            if (bookings.Count != ids.Count)
                throw Unresolved("stays");

            var payments = await db.Payments.AsNoTracking()
                .Where(p => p.OrgId == orgId && ids.Contains(p.BookingId) && CollectedStatuses.Contains(p.Status))
                .Select(p => new { p.Id, p.BookingId, InCasaZen = p.StripePaymentIntentId != null, Withheld = p.OtaWithholdingTax > 0m || p.WithholdingTaxApplied })
                .ToListAsync(ct);
            var owner = await db.PropertyOwners.AsNoTracking()
                .Where(o => o.OrgId == orgId && o.Id == ownerId)
                .Select(o => new { o.EntityKind, o.TaxRegime })
                .FirstOrDefaultAsync(ct);
            var ownerPrivate = owner is not null && OwnerTaxRules.IsNaturalPrivate(owner.EntityKind, owner.TaxRegime)
                && profile?.EntityKind != OwnerEntityKind.LegalEntity;
            var treatments = (await taxChecks.TreatmentOfStaysAsync(orgId, ownerId, ids, paidOn, ct)).ToDictionary(t => t.BookingId);
            var accounts = await holders.ForOrgAsync(orgId, ct);

            foreach (var (id, stay) in stays.OrderBy(s => bookings[s.Key].CheckOut).ThenBy(s => s.Key))
            {
                var booking = bookings[id];
                var input = new PayoutStayInput(
                    id,
                    stay.PropertyId ?? booking.PropertyId,
                    booking.Source,
                    AlloggiatiStatusRules.ActiveBookingStatuses.Contains(booking.Status) && booking.NoShowAt is null,
                    string.Equals(booking.Country?.Trim(), FiscalDocumentRules.Italy, StringComparison.OrdinalIgnoreCase),
                    DateOnly.FromDateTime(booking.CheckIn),
                    DateOnly.FromDateTime(booking.CheckOut),
                    stay.SharePercent,
                    stay.Currency,
                    stay.Gross,
                    booking.TouristTax,
                    stay.FromPayments,
                    payments.Any(p => stay.PaymentIds.Contains(p.Id) && p.InCasaZen),
                    booking.OtaCompanyCollect,
                    accounts.Resolve(stay.PropertyId ?? booking.PropertyId, booking.Source),
                    payments.Any(p => p.BookingId == id && p.Withheld),
                    ownerPrivate,
                    treatments.GetValueOrDefault(id));
                var fact = PayoutFactRules.EvaluateStay(input, settings);
                facts.Add(fact.InBase ? fact with { BaseAmount = InEuro(stay, rates, paidOn) } : fact);
            }
        }

        var inBase = facts.Where(s => s.InBase).ToList();
        return new PayoutFacts
        {
            Currency = currency,
            AgencyCollected = facts.Count == 0 ? null : facts.Any(s => s.CollectedByAgency),
            QualifyingShortLet = facts.Count == 0 ? null : facts.Any(s => s.Qualifying),
            Components = new Dictionary<string, decimal>(StringComparer.Ordinal) { [PayoutFactRules.StayGross] = inBase.Sum(s => s.BaseAmount ?? 0m) },
            StayIds = [.. facts.Select(s => s.BookingId)],
            Stays = facts,
            PortalsWithholding = [.. portals.Select(p => p.ToString()).Order(StringComparer.Ordinal)],
            Fx = fx,
            Parameters = rule,
        };
    }

    /// <summary>
    /// The stays of the payout: the owner's revenue lines of the ledger of the version's month and of the months before (posted up to the cut-off) that no confirmed
    /// payout of the owner covered, added up per stay; the stays with something left to decide.
    /// </summary>
    private async Task<Dictionary<Guid, StayRevenue>> StaysOfAsync(OwnerStatementVersion version, CancellationToken ct)
    {
        var orgId = version.OrgId;
        var ownerId = version.PropertyOwnerId;
        var lines = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.PropertyOwnerId == ownerId && e.EntryType == OwnerLedgerEntryType.BookingRevenue
                && (e.SourceType == OwnerLedgerSourceType.Payment || e.SourceType == OwnerLedgerSourceType.Booking)
                && e.PostedAt <= version.CutoffAt
                && (e.PeriodYear < version.PeriodYear || (e.PeriodYear == version.PeriodYear && e.PeriodMonth <= version.PeriodMonth)))
            .Select(e => new RevenueLine(
                e.SourceType, e.SourceId, e.SourceReference, e.PropertyId, e.OriginalCurrency, e.OriginalAmount, e.SharePercent, e.PeriodYear, e.PeriodMonth, e.PostedAt))
            .ToListAsync(ct);

        // What the confirmed payouts of the owner decided: the lines of their version, its month and the months before, posted up to its cut-off. A line is decided once.
        var covers = await db.OwnerPayouts.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.PropertyOwnerId == ownerId && p.Status == OwnerPayoutStatus.Confirmed)
            .Join(
                db.OwnerStatementVersions.AsNoTracking().Where(v => v.OrgId == orgId),
                p => p.OwnerStatementVersionId,
                v => v.Id,
                (p, v) => new { Period = v.PeriodYear * 12 + v.PeriodMonth, v.CutoffAt })
            .ToListAsync(ct);

        var stays = new Dictionary<Guid, StayRevenue>();
        var open = lines.Where(l => !covers.Any(c => l.PeriodYear * 12 + l.PeriodMonth <= c.Period && l.PostedAt <= c.CutoffAt));
        foreach (var group in open.GroupBy(StayOf))
        {
            var stayId = group.Key ?? throw Unresolved("stays");
            var currencies = group.Select(l => StayCurrency.Normalize(l.Currency)).Distinct(StringComparer.Ordinal).ToList();
            if (currencies.Count != 1)
                throw Unresolved("stays");

            var gross = group.Sum(l => l.Amount);
            if (gross <= 0m)
                continue;

            var fromPayments = group.Where(l => l.SourceType == OwnerLedgerSourceType.Payment).Sum(l => l.Amount) > 0m;
            stays[stayId] = new StayRevenue(
                group.Select(l => l.PropertyId).FirstOrDefault(p => p is not null),
                currencies[0],
                gross,
                group.Where(l => l.Amount > 0m).Select(l => l.SharePercent).DefaultIfEmpty(100m).Max(),
                fromPayments,
                [.. group.Where(l => l.SourceType == OwnerLedgerSourceType.Payment && l.SourceId is not null).Select(l => l.SourceId!.Value).Distinct()]);
        }

        return stays;
    }

    /// <summary>The gross of a stay of the base in euro: as it is, or at the agency's rate of the payout day (half to even, as the ledger converts).</summary>
    private static decimal InEuro(StayRevenue stay, IReadOnlyList<OwnerFxRate> rates, DateOnly paidOn)
    {
        if (string.Equals(stay.Currency, StayCurrency.Euro, StringComparison.Ordinal))
            return stay.Gross;
        var quote = OwnerAccountingService.RateFor(rates, stay.Currency, StayCurrency.Euro, paidOn) ?? throw Unresolved("fxRate");
        return Math.Round(stay.Gross * quote.Multiplier, 2, MidpointRounding.ToEven);
    }

    /// <summary>The stay of a revenue line: the booking itself, or the booking a payment line names in its reference.</summary>
    private static Guid? StayOf(RevenueLine line) =>
        line.SourceType == OwnerLedgerSourceType.Booking ? line.SourceId
        : Guid.TryParseExact(line.Reference, "N", out var booking) ? booking : null;

    private static DomainRuleException Unresolved(string field) =>
        new(OwnerPayoutErrors.TaxApplicabilityUnresolved, "TaxApplicabilityUnresolved") { Extensions = new Dictionary<string, object?> { ["field"] = field } };

    private sealed record RevenueLine(
        OwnerLedgerSourceType SourceType,
        Guid? SourceId,
        string? Reference,
        Guid? PropertyId,
        string Currency,
        decimal Amount,
        decimal SharePercent,
        int PeriodYear,
        int PeriodMonth,
        DateTime PostedAt);

    /// <summary>The owner's revenue of one stay: its property, currency and gross (the original amounts), its share, whether payments posted it and which.</summary>
    private sealed record StayRevenue(Guid? PropertyId, string Currency, decimal Gross, decimal SharePercent, bool FromPayments, IReadOnlyList<Guid> PaymentIds);

    private sealed record BookingFacts(
        Guid Id,
        Guid PropertyId,
        BookingSource Source,
        BookingStatus Status,
        DateTime? NoShowAt,
        DateTime CheckIn,
        DateTime CheckOut,
        bool? OtaCompanyCollect,
        decimal TouristTax,
        string? Country);
}
