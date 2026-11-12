using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>The plan of the lines of a month: what the sources of the month say the ledger must contain.</summary>
public sealed partial class OwnerAccountingService
{
    private async Task<Plan> PlanAsync(Guid orgId, Period period, CancellationToken ct)
    {
        var plan = new Plan();

        // The completed payments that may be recognized in the month: paid in it, or paid before the end of it for a stay that checks out in it.
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.Status == PaymentStatus.Completed && p.ProcessedAt != null && p.ProcessedAt < period.EndUtc
                && (p.ProcessedAt >= period.StartUtc || (p.Booking.CheckOutDate >= period.StartUtc && p.Booking.CheckOutDate < period.EndUtc)))
            .Select(p => new PaymentSource(
                p.Id, p.BookingId, p.Booking.PropertyId, p.Amount, p.Amount - p.RefundedAmount, p.ProcessedAt!.Value,
                p.Booking.CheckInDate, p.Booking.CheckOutDate, p.Booking.Property.Currency))
            .ToListAsync(ct);

        // The channel commission of an active booking whose payments are gross, with the first completed payment of the booking.
        var bookingIds = payments.Select(p => p.BookingId).Distinct().ToList();
        var channelFees = (await db.Bookings.AsNoTracking()
                .Where(b => bookingIds.Contains(b.Id) && b.OtaCommission > 0m && b.OtaCommissionIncludedInPayments == true
                    && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn || b.Status == BookingStatus.CheckedOut))
                .Select(b => new
                {
                    b.Id,
                    Amount = b.OtaCommission!.Value,
                    FirstPaymentId = b.Payments
                        .Where(p => p.Status == PaymentStatus.Completed && p.ProcessedAt != null)
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

        var wires = await db.WireBatchStatements.AsNoTracking()
            .Where(w => w.OrgId == orgId && w.WireBatch.Status == WireBatchStatus.Paid && w.WireBatch.PaidAt >= period.StartUtc
                && w.WireBatch.PaidAt < period.EndUtc && w.OwnerStatement.NetPayable > 0m)
            .Select(w => new WireSource(
                w.Id, w.WireBatchId, w.WireBatch.PaidAt!.Value, w.WireBatch.Currency, w.OwnerStatement.PropertyOwnerId, w.OwnerStatement.PropertyId,
                w.OwnerStatement.NetPayable))
            .ToListAsync(ct);

        var propertyIds = payments.Select(p => p.PropertyId).Concat(charges.Select(c => c.PropertyId)).Distinct().ToList();
        var ownerships = await db.PropertyOwnerships.AsNoTracking()
            .Where(o => o.OrgId == orgId && propertyIds.Contains(o.PropertyId))
            .ToListAsync(ct);
        var ownershipIds = ownerships.Select(o => o.Id).ToList();
        var mandates = await db.OwnerMandates.AsNoTracking()
            .Where(m => ownershipIds.Contains(m.PropertyOwnershipId))
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
                if (plan.ClosedOwners.Contains(share.OwnerId))
                    continue;
                var context = await ContextAsync(orgId, share.OwnerId, period, plan, contexts, ct);
                var checkOut = DateOnly.FromDateTime(payment.CheckOut);
                var recognized = context.StayRecognition && checkOut > paidOn ? checkOut : paidOn;
                if (!period.Contains(recognized))
                    continue;

                var source = new Source(
                    OwnerLedgerEntryType.BookingRevenue, OwnerLedgerSourceType.Payment, payment.Id, payment.BookingId.ToString("N"), "pay:" + payment.Id.ToString("N"),
                    payment.PropertyId, currency, payment.Net, DateOnly.FromDateTime(payment.CheckIn), recognized, paidOn, paidOn);
                builder.Add(source, share, revenue[i], context);

                var mandate = MandateOn(mandates, share.OwnershipId, recognized);
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
                if (plan.ClosedOwners.Contains(shares[i].OwnerId))
                    continue;
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
            if (plan.ClosedOwners.Contains(wire.OwnerId))
                continue;
            var day = DateOnly.FromDateTime(wire.PaidAt);
            var context = await ContextAsync(orgId, wire.OwnerId, period, plan, contexts, ct);
            var source = new Source(
                OwnerLedgerEntryType.OwnerPayment, OwnerLedgerSourceType.WireBatch, wire.WireBatchId, wire.Id.ToString("N"), "wire:" + wire.Id.ToString("N"),
                wire.PropertyId, StayCurrency.Normalize(wire.Currency), -wire.Amount, day, day, day, day);
            builder.Add(source, new Share(wire.OwnerId, null, 100m), -wire.Amount, context);
        }

        return plan;
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

    /// <summary>The mandate of the ownership in force on the day, the latest first.</summary>
    private static OwnerMandate? MandateOn(IReadOnlyList<OwnerMandate> mandates, Guid? ownershipId, DateOnly day) =>
        ownershipId is null
            ? null
            : mandates
                .Where(m => m.PropertyOwnershipId == ownershipId && DateOnly.FromDateTime(m.EffectiveFrom) <= day
                    && (m.EffectiveUntil == null || DateOnly.FromDateTime(m.EffectiveUntil.Value) > day))
                .MaxBy(m => m.EffectiveFrom);

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
        public List<OwnerLedgerEntry> Entries { get; } = [];

        public List<OwnerReadinessIssue> Issues { get; } = [];

        public HashSet<Guid> ClosedOwners { get; } = [];
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
                IdempotencyKey = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{source.Group}:{(int)source.Type}:{share.OwnerId:N}:{Cents(original)}:{statementCurrency}:{Cents(amount)}"),
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

    private sealed record WireSource(Guid Id, Guid WireBatchId, DateTime PaidAt, string? Currency, Guid OwnerId, Guid PropertyId, decimal Amount);
}
