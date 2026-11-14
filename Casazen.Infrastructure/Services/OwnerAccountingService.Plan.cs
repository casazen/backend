using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>The plan of the lines of a month: what the sources of the month say the ledger must contain.</summary>
public sealed partial class OwnerAccountingService
{
    private async Task<Plan> PlanAsync(Guid orgId, Period period, CancellationToken ct)
    {
        var plan = new Plan();

        // The collected payments that may be recognized in the month: paid in it, or paid before the end of it for a stay that checks out in it. A refund moves
        // a payment to PartiallyRefunded or Refunded (PaymentRefundService.RecomputePaymentAsync), so every collected state is read and the revenue is the net
        // of the refunds: a partial refund lowers the line (reversal + replacement), a full one leaves nothing to share.
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.OrgId == orgId && CollectedStatuses.Contains(p.Status) && p.ProcessedAt != null && p.ProcessedAt < period.EndUtc
                && (p.ProcessedAt >= period.StartUtc || (p.Booking.CheckOutDate >= period.StartUtc && p.Booking.CheckOutDate < period.EndUtc)))
            .Select(p => new PaymentSource(
                p.Id, p.BookingId, p.Booking.PropertyId, p.Amount, p.Amount - p.RefundedAmount, p.ProcessedAt!.Value,
                p.Booking.CheckInDate, p.Booking.CheckOutDate, p.Booking.Property.Currency))
            .ToListAsync(ct);

        // The channel commission of an active booking whose payments are gross, with the first payment of the booking that still holds money. «Active» is the one
        // definition of the code (a status that owes the police communication and no no-show: AlloggiatiStatusRules), never a list of its own.
        var bookingIds = payments.Select(p => p.BookingId).Distinct().ToList();
        var channelFees = (await db.Bookings.AsNoTracking()
                .Where(b => bookingIds.Contains(b.Id) && b.OtaCommission > 0m && b.OtaCommissionIncludedInPayments == true
                    && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) && b.NoShowAt == null)
                .Select(b => new
                {
                    b.Id,
                    Amount = b.OtaCommission!.Value,
                    FirstPaymentId = b.Payments
                        .Where(p => (p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded) && p.ProcessedAt != null)
                        .OrderBy(p => p.ProcessedAt)
                        .ThenBy(p => p.Id)
                        .Select(p => p.Id)
                        .FirstOrDefault(),
                })
                .ToListAsync(ct))
            .ToDictionary(b => b.Id);

        var charges = await db.OwnerCharges.AsNoTracking()
            .Where(c => c.OrgId == orgId && c.Status == OwnerChargeStatus.Approved && c.EffectiveFrom <= period.Last
                && (c.EffectiveTo == null || c.EffectiveTo >= period.First))
            .ToListAsync(ct);

        // The wires marked paid. A month an owner settled with a confirmed payout is settled by that payout (it carries the tax decision and posts its own lines):
        // the wires of the same statement month are not posted, so one transfer is never debited twice (OwnerPayoutService, E06/F05/US02/T03).
        var wireSources = await db.WireBatchStatements.AsNoTracking()
            .Where(w => w.OrgId == orgId && w.WireBatch.Status == WireBatchStatus.Paid && w.WireBatch.PaidAt >= period.StartUtc
                && w.WireBatch.PaidAt < period.EndUtc && w.OwnerStatement.NetPayable > 0m)
            .Select(w => new WireSource(
                w.Id, w.WireBatchId, w.WireBatch.PaidAt!.Value, w.WireBatch.Currency, w.OwnerStatement.PropertyOwnerId, w.OwnerStatement.PropertyId,
                w.OwnerStatement.NetPayable, w.OwnerStatement.Year, w.OwnerStatement.Month))
            .ToListAsync(ct);
        var settledByPayout = wireSources.Count == 0 ? new HashSet<(Guid, int, int)>() : await PayoutSettledMonthsAsync(orgId, ct);
        var wires = wireSources.Where(w => !settledByPayout.Contains((w.OwnerId, w.StatementYear, w.StatementMonth))).ToList();

        var propertyIds = payments.Select(p => p.PropertyId).Concat(charges.Select(c => c.PropertyId)).Distinct().ToList();
        var ownerships = await db.PropertyOwnerships.AsNoTracking()
            .Where(o => o.OrgId == orgId && propertyIds.Contains(o.PropertyId))
            .ToListAsync(ct);
        foreach (var group in ownerships.GroupBy(o => o.PropertyOwnerId))
            plan.OwnerProperties[group.Key] = group.Select(o => o.PropertyId).ToHashSet();
        // The mandates of the owners (E13/F01/US01/T01) and those of single ownerships of before: OwnerMandateRules.InForce picks the one of a share.
        var mandates = await db.OwnerMandates.AsNoTracking()
            .Where(m => m.OrgId == orgId)
            .ToListAsync(ct);

        var earliest = payments.Select(p => DateOnly.FromDateTime(p.PaidAt)).Append(period.First).Min().AddDays(-FxMaxAgeDays);
        var rates = await db.OwnerFxRates.AsNoTracking()
            .Where(r => r.OrgId == orgId && r.RateDate >= earliest && r.RateDate <= period.Last)
            .ToListAsync(ct);

        plan.ClosedOwners.UnionWith(await db.OwnerClosePeriods.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.PeriodYear == period.Year && p.PeriodMonth == period.Month && p.Status == OwnerClosePeriodStatus.Closed)
            .Select(p => p.PropertyOwnerId)
            .ToListAsync(ct));

        var contexts = new Dictionary<Guid, OwnerContext>();
        var builder = new PlanBuilder(orgId, plan, rates, period);

        foreach (var payment in payments)
        {
            if (payment.Gross <= 0m)
            {
                plan.Issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.SourceAmountMissing, PropertyId: payment.PropertyId, SourceId: payment.Id));
                continue;
            }

            // Refunded in full: no revenue left to share.
            if (payment.Net <= 0m)
                continue;

            var paidOn = DateOnly.FromDateTime(payment.PaidAt);
            var shares = SharesOn(plan, ownerships, payment.PropertyId, paidOn, payment.Id);
            if (shares is null)
                continue;

            var currency = StayCurrency.Normalize(payment.Currency);
            var revenue = Split(payment.Net, shares);
            var fee = channelFees.TryGetValue(payment.BookingId, out var booking) && booking.FirstPaymentId == payment.Id ? booking : null;
            var fees = fee is null ? null : Split(fee.Amount, shares);
            for (var i = 0; i < shares.Count; i++)
            {
                var share = shares[i];
                var context = await ContextAsync(orgId, share.OwnerId, period, plan, contexts, ct);
                var checkOut = DateOnly.FromDateTime(payment.CheckOut);
                var recognized = context.StayRecognition && checkOut > paidOn ? checkOut : paidOn;
                if (!period.Contains(recognized))
                    continue;

                var source = new Source(
                    OwnerLedgerEntryType.BookingRevenue, OwnerLedgerSourceType.Payment, payment.Id, payment.BookingId.ToString("N"), "pay:" + payment.Id.ToString("N"),
                    payment.PropertyId, currency, payment.Net, DateOnly.FromDateTime(payment.CheckIn), recognized, paidOn, paidOn);
                builder.Add(source, share, revenue[i], context);

                var mandate = OwnerMandateRules.InForce(mandates, share.OwnerId, share.OwnershipId, recognized);
                if (mandate is { CommissionPercent: > 0m })
                    builder.Add(source with { Type = OwnerLedgerEntryType.AgencyCommission }, share, -Round(revenue[i] * mandate.CommissionPercent / 100m), context);

                if (fee is not null && fees is not null)
                {
                    builder.Add(
                        source with
                        {
                            Type = OwnerLedgerEntryType.ChannelFee,
                            SourceType = OwnerLedgerSourceType.Booking,
                            SourceId = payment.BookingId,
                            Group = "fee:" + payment.BookingId.ToString("N"),
                            SourceAmount = -fee.Amount,
                        },
                        share,
                        -fees[i],
                        context);
                }
            }
        }

        foreach (var charge in charges)
        {
            var day = OccurrenceIn(charge, period);
            if (day is null)
                continue;
            List<Share>? shares = charge.PropertyOwnerId is { } single ? [new Share(single, null, 100m)] : SharesOn(plan, ownerships, charge.PropertyId, day.Value, charge.Id);
            if (shares is null)
                continue;

            var month = string.Create(CultureInfo.InvariantCulture, $"{period.Year:D4}-{period.Month:D2}");
            var split = Split(charge.Amount, shares);
            for (var i = 0; i < shares.Count; i++)
            {
                var context = await ContextAsync(orgId, shares[i].OwnerId, period, plan, contexts, ct);
                var source = new Source(
                    charge.Kind == OwnerChargeKind.Expense ? OwnerLedgerEntryType.OwnerExpense : OwnerLedgerEntryType.OwnerCharge,
                    OwnerLedgerSourceType.OwnerCharge, charge.Id, month, "charge:" + charge.Id.ToString("N") + ":" + month,
                    charge.PropertyId, StayCurrency.Normalize(charge.Currency), -charge.Amount, day.Value, day.Value, null, day.Value,
                    charge.Description, charge.AttachmentReference);
                builder.Add(source, shares[i], -split[i], context);
            }
        }

        foreach (var wire in wires)
        {
            var day = DateOnly.FromDateTime(wire.PaidAt);
            var context = await ContextAsync(orgId, wire.OwnerId, period, plan, contexts, ct);
            var source = new Source(
                OwnerLedgerEntryType.OwnerPayment, OwnerLedgerSourceType.WireBatch, wire.WireBatchId, wire.Id.ToString("N"), "wire:" + wire.Id.ToString("N"),
                wire.PropertyId, StayCurrency.Normalize(wire.Currency), -wire.Amount, day, day, day, day);
            builder.Add(source, new Share(wire.OwnerId, null, 100m), -wire.Amount, context);
        }

        await AddLateSourceIssuesAsync(orgId, period, plan, ct);
        return plan;
    }

    /// <summary>
    /// A closed month of an owner is never posted again, but a source that arrives or changes after the close must not vanish: the lines the sources say
    /// the month should hold, and the ledger does not hold, are a <see cref="OwnerReadinessCodes.LateSourceAfterClose"/> issue (not blocking: the close
    /// stays; a correction in a later month fixes the owner's account).
    /// </summary>
    private async Task AddLateSourceIssuesAsync(Guid orgId, Period period, Plan plan, CancellationToken ct)
    {
        if (plan.ClosedOwners.Count == 0)
            return;

        var liveKeys = (await LiveEntriesAsync(orgId, period, ct))
            .Where(e => plan.ClosedOwners.Contains(e.PropertyOwnerId))
            .Select(e => BaseKey(e.IdempotencyKey))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var late in plan.Entries.Where(e => plan.ClosedOwners.Contains(e.PropertyOwnerId) && !liveKeys.Contains(e.IdempotencyKey)))
            plan.Issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.LateSourceAfterClose, late.PropertyOwnerId, late.PropertyId, late.SourceId, Blocking: false));
    }

    /// <summary>
    /// The (owner, year, month) of the statements settled by a confirmed payout: the payout is the record of the payment to the owner for that month, so a
    /// wire of the same month's statements is neither posted nor counted at the close.
    /// </summary>
    private async Task<HashSet<(Guid, int, int)>> PayoutSettledMonthsAsync(Guid orgId, CancellationToken ct)
    {
        var rows = await db.OwnerPayouts.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Status == OwnerPayoutStatus.Confirmed)
            .Join(db.OwnerStatementVersions, p => p.OwnerStatementVersionId, v => v.Id, (p, v) => new { p.PropertyOwnerId, v.PeriodYear, v.PeriodMonth })
            .Distinct()
            .ToListAsync(ct);
        return rows.Select(r => (r.PropertyOwnerId, r.PeriodYear, r.PeriodMonth)).ToHashSet();
    }

    /// <summary>
    /// What the owner's profile says for the month: the statement currency, the recognition rule and the rule version. An owner the profile cannot
    /// resolve is not hidden: the code of the reason is a blocking issue of the month, and its lines carry no rule version.
    /// </summary>
    private async Task<OwnerContext> ContextAsync(
        Guid orgId,
        Guid ownerId,
        Period period,
        Plan plan,
        Dictionary<Guid, OwnerContext> cache,
        CancellationToken ct)
    {
        if (cache.TryGetValue(ownerId, out var known))
            return known;

        OwnerContext context;
        try
        {
            var resolution = await fiscal.ResolveAsync(orgId, ownerId, period.Last, ct);
            context = new OwnerContext(
                StayCurrency.Normalize(resolution.Profile.SettlementCurrency),
                string.Equals(resolution.Profile.RevenueRecognitionRule, StayRecognitionRule, StringComparison.OrdinalIgnoreCase),
                Truncate(resolution.Policy.RuleCode + "@" + resolution.Policy.Version, OwnerLedgerEntry.RuleVersionMaxLength));
        }
        catch (DomainRuleException e)
        {
            plan.Issues.Add(new OwnerReadinessIssue(e.Code, ownerId));
            context = new OwnerContext(null, false, null);
        }

        cache[ownerId] = context;
        return context;
    }

    /// <summary>The owners of the property on the day, by owner id; <c>null</c> with an issue when there is none or the shares do not make 100.</summary>
    private static List<Share>? SharesOn(Plan plan, IReadOnlyList<PropertyOwnership> ownerships, Guid propertyId, DateOnly day, Guid sourceId)
    {
        var active = ownerships
            .Where(o => o.PropertyId == propertyId && DateOnly.FromDateTime(o.OwnershipStart) <= day
                && (o.OwnershipEnd == null || DateOnly.FromDateTime(o.OwnershipEnd.Value) > day))
            .OrderBy(o => o.PropertyOwnerId)
            .ToList();
        if (active.Count == 0)
        {
            plan.Issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.OwnershipMissing, PropertyId: propertyId, SourceId: sourceId));
            return null;
        }

        if (active.Sum(o => o.SharePercent) != 100m)
        {
            plan.Issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.OwnershipShareGap, PropertyId: propertyId, SourceId: sourceId));
            return null;
        }

        return active.Select(o => new Share(o.PropertyOwnerId, o.Id, o.SharePercent)).ToList();
    }

    /// <summary>The amount split by the shares, each rounded half to even, the last share taking the remainder: the parts add up to the amount.</summary>
    internal static decimal[] Split(decimal amount, IReadOnlyList<Share> shares)
    {
        var parts = new decimal[shares.Count];
        var assigned = 0m;
        for (var i = 0; i < shares.Count - 1; i++)
        {
            parts[i] = Round(amount * shares[i].Percent / 100m);
            assigned += parts[i];
        }

        parts[^1] = amount - assigned;
        return parts;
    }

    /// <summary>The day a charge is posted in the month: its first day for a one-time charge, the first day of the month (or its own) for a monthly one.</summary>
    private static DateOnly? OccurrenceIn(OwnerCharge charge, Period period)
    {
        if (charge.Recurrence == OwnerChargeRecurrence.OneTime)
            return period.Contains(charge.EffectiveFrom) ? charge.EffectiveFrom : null;
        var day = charge.EffectiveFrom > period.First ? charge.EffectiveFrom : period.First;
        return day <= period.Last && (charge.EffectiveTo is null || day <= charge.EffectiveTo) ? day : null;
    }

    /// <summary>
    /// The multiplier from one currency to another on a day, from the rates of the agency of that day or of the <see cref="FxMaxAgeDays"/> before (direct, or
    /// the inverse rounded to 8 decimals); <c>null</c> without one.
    /// </summary>
    internal static FxQuote? RateFor(IReadOnlyList<OwnerFxRate> rates, string from, string to, DateOnly day)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
            return new FxQuote(1m, null, null);
        var oldest = day.AddDays(-FxMaxAgeDays);
        var direct = rates
            .Where(r => r.BaseCurrency == from && r.QuoteCurrency == to && r.RateDate <= day && r.RateDate >= oldest)
            .MaxBy(r => r.RateDate);
        if (direct is not null)
            return new FxQuote(direct.Rate, direct.Source, direct.RateDate);
        var inverse = rates
            .Where(r => r.BaseCurrency == to && r.QuoteCurrency == from && r.RateDate <= day && r.RateDate >= oldest)
            .MaxBy(r => r.RateDate);
        return inverse is null ? null : new FxQuote(Math.Round(1m / inverse.Rate, 8, MidpointRounding.ToEven), inverse.Source, inverse.RateDate);
    }

    private sealed class Plan
    {
        /// <summary>The lines the sources say the month holds, those of the closed owners included (they are only compared, never posted).</summary>
        public List<OwnerLedgerEntry> Entries { get; } = [];

        public List<OwnerReadinessIssue> Issues { get; } = [];

        public HashSet<Guid> ClosedOwners { get; } = [];

        /// <summary>The properties each owner has (or had, or will have) an ownership of: an issue of a property without an owner concerns all of them.</summary>
        public Dictionary<Guid, HashSet<Guid>> OwnerProperties { get; } = [];

        /// <summary>The lines that may be posted: those of the owners whose month is still open.</summary>
        public IEnumerable<OwnerLedgerEntry> OpenEntries => Entries.Where(e => !ClosedOwners.Contains(e.PropertyOwnerId));
    }

    /// <summary>Builds the planned lines: the conversion to the statement currency and the key of the facts.</summary>
    private sealed class PlanBuilder(Guid orgId, Plan plan, IReadOnlyList<OwnerFxRate> rates, Period period)
    {
        public void Add(Source source, Share share, decimal original, OwnerContext context)
        {
            var statementCurrency = context.Currency ?? source.Currency;
            var fx = RateFor(rates, source.Currency, statementCurrency, source.FxDay);
            if (fx is null)
            {
                plan.Issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.FxMissing, share.OwnerId, source.PropertyId, source.SourceId));
                return;
            }

            var amount = Round(original * fx.Multiplier);
            plan.Entries.Add(new OwnerLedgerEntry
            {
                OrgId = orgId,
                PropertyOwnerId = share.OwnerId,
                PropertyId = source.PropertyId,
                EntryType = source.Type,
                SourceType = source.SourceType,
                SourceId = source.SourceId,
                SourceReference = source.Reference,
                AttachmentReference = source.Attachment,
                // A line computed without a rule (the owner's profile or rule set cannot be resolved yet) has its own key: once the owner can be resolved the
                // key changes, so the line is reversed and replaced by one that carries the rule version (AC-NF110-1), never left without it.
                IdempotencyKey = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{source.Group}:{(int)source.Type}:{share.OwnerId:N}:{Cents(original)}:{statementCurrency}:{Cents(amount)}{(context.RuleVersion is null ? NoRuleKeySuffix : string.Empty)}"),
                ServiceDate = source.ServiceDate,
                RecognitionDate = source.RecognitionDate,
                PaymentDate = source.PaymentDate,
                PeriodYear = period.Year,
                PeriodMonth = period.Month,
                PropertyOwnershipId = share.OwnershipId,
                SharePercent = share.Percent,
                OriginalCurrency = source.Currency,
                SourceAmount = source.SourceAmount,
                OriginalAmount = original,
                StatementCurrency = statementCurrency,
                FxRate = fx.Multiplier,
                FxSource = fx.Source,
                FxDate = fx.Day,
                RuleVersion = context.RuleVersion,
                Amount = amount,
                Description = Truncate(source.Description, OwnerLedgerEntry.TextMaxLength),
            });
        }

        private static long Cents(decimal value) => decimal.ToInt64(Math.Round(value * 100m, 0, MidpointRounding.ToEven));
    }

    internal sealed record Share(Guid OwnerId, Guid? OwnershipId, decimal Percent);

    internal sealed record FxQuote(decimal Multiplier, string? Source, DateOnly? Day);

    private sealed record OwnerContext(string? Currency, bool StayRecognition, string? RuleVersion);

    private sealed record Source(
        OwnerLedgerEntryType Type,
        OwnerLedgerSourceType SourceType,
        Guid SourceId,
        string? Reference,
        string Group,
        Guid? PropertyId,
        string Currency,
        decimal SourceAmount,
        DateOnly ServiceDate,
        DateOnly RecognitionDate,
        DateOnly? PaymentDate,
        DateOnly FxDay,
        string? Description = null,
        string? Attachment = null);

    private sealed record PaymentSource(
        Guid Id,
        Guid BookingId,
        Guid PropertyId,
        decimal Gross,
        decimal Net,
        DateTime PaidAt,
        DateTime CheckIn,
        DateTime CheckOut,
        string? Currency);

    private sealed record WireSource(
        Guid Id,
        Guid WireBatchId,
        DateTime PaidAt,
        string? Currency,
        Guid OwnerId,
        Guid PropertyId,
        decimal Amount,
        int StatementYear,
        int StatementMonth);
}
