using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>The plan of the lines of a month: what the sources of the month say the ledger must contain.</summary>
public sealed partial class OwnerAccountingService
{
    /// <summary>The portals (<see cref="ChannelCommissionRules.Portals"/>), as an array a query can test a booking source against.</summary>
    private static readonly BookingSource[] PortalSources = [.. ChannelCommissionRules.Portals];

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
                p.Booking.CheckInDate, p.Booking.CheckOutDate, p.Booking.Property.Currency,
                p.Booking.TouristTax, p.Booking.TotalPrice, p.Booking.BasePrice, p.Booking.CleaningFee, p.ProviderFeeAmount, p.ProviderBalanceCurrency))
            .ToListAsync(ct);

        // The stays that check out in the month (E13/F01/US01/T02): the active bookings only («active» is the one definition of the code, a status that owes the
        // police communication and no no-show: AlloggiatiStatusRules, never a list of its own). A stay without a collected payment (a portal, the agency's own
        // site, a booking entered by hand) is posted from its amounts; every one of them gets the flat fees of the mandate.
        var stays = await db.Bookings.AsNoTracking()
            .Where(b => b.OrgId == orgId && b.CheckOutDate >= period.StartUtc && b.CheckOutDate < period.EndUtc
                && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) && b.NoShowAt == null)
            .Select(b => new StaySource(
                b.Id, b.PropertyId, b.Source, b.BasePrice, b.CleaningFee, b.OtaCommission, b.CheckInDate, b.CheckOutDate, b.Property.Currency,
                b.Payments.Any(p => CollectedStatuses.Contains(p.Status) && p.ProcessedAt != null), b.TotalPrice, b.TouristTax, b.PriceOverridden))
            .ToListAsync(ct);

        // The channel commission of a booking on the payment path, with its first payment that still holds money: a portal booking (the commission of the
        // reservation, else the rule of the agency or the default, ⚖ R2), or another booking whose reservation brought a commission already taken out of the
        // payments. Active bookings only.
        var bookingIds = payments.Select(p => p.BookingId).Distinct().ToList();
        var feeBookings = (await db.Bookings.AsNoTracking()
                .Where(b => bookingIds.Contains(b.Id) && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) && b.NoShowAt == null
                    && (PortalSources.Contains(b.Source) || (b.OtaCommission > 0m && b.OtaCommissionIncludedInPayments == true)))
                .Select(b => new
                {
                    Stay = new StaySource(
                        b.Id, b.PropertyId, b.Source, b.BasePrice, b.CleaningFee, b.OtaCommission, b.CheckInDate, b.CheckOutDate, b.Property.Currency, true,
                        b.TotalPrice, b.TouristTax, b.PriceOverridden),
                    FirstPaymentId = b.Payments
                        .Where(p => (p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded) && p.ProcessedAt != null)
                        .OrderBy(p => p.ProcessedAt)
                        .ThenBy(p => p.Id)
                        .Select(p => p.Id)
                        .FirstOrDefault(),
                })
                .ToListAsync(ct))
            .ToDictionary(b => b.Stay.Id, b => new FeeBooking(b.Stay, b.FirstPaymentId));

        // Only one path ever posts a stay: a stay whose revenue the booking path already posted in another month (it was not paid then) keeps that line, its
        // payment does not post it again (E13/F01/US01/T02).
        var recognizedFromBooking = await StaysPostedFromBookingElsewhereAsync(orgId, period, bookingIds, ct);

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

        var propertyIds = payments.Select(p => p.PropertyId)
            .Concat(charges.Select(c => c.PropertyId))
            .Concat(stays.Select(s => s.PropertyId))
            .Distinct()
            .ToList();
        var ownerships = await db.PropertyOwnerships.AsNoTracking()
            .Where(o => o.OrgId == orgId && propertyIds.Contains(o.PropertyId))
            .ToListAsync(ct);
        foreach (var group in ownerships.GroupBy(o => o.PropertyOwnerId))
            plan.OwnerProperties[group.Key] = group.Select(o => o.PropertyId).ToHashSet();

        // The mandates of the owners (E13/F01/US01/T01) and those of single ownerships of before: OwnerMandateRules.InForce picks the one of a share. The
        // commission rules of the channels and their configured defaults (⚖ R2).
        var mandates = await db.OwnerMandates.AsNoTracking()
            .Where(m => m.OrgId == orgId)
            .ToListAsync(ct);
        var channelRules = await db.OrgChannelCommissionRules.AsNoTracking()
            .Where(r => r.OrgId == orgId)
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
        var inputs = new PlanInputs(
            orgId, period, plan, builder, contexts, ownerships, mandates, channelRules, accountingOptions?.Value ?? new OwnerAccountingOptions());

        foreach (var payment in payments)
        {
            if (recognizedFromBooking.Contains(payment.BookingId))
                continue;

            await PlanPaymentAsync(inputs, payment, feeBookings.GetValueOrDefault(payment.BookingId), ct);
        }

        foreach (var stay in stays)
            await PlanStayAsync(inputs, stay, ct);

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
    /// The lines of a collected payment (a card payment through CasaZen or one the agency recorded): its revenue net of the refunds and of its part of the
    /// tourist tax (<see cref="OwnerLedgerRules.RevenueOfPayment"/>), recognized on the payment day or on the check-out for a profile with the rule
    /// <c>stay</c>; the commission of the mandate in force that day on its base; the card fee when the mandate passes it to the owner; with the first payment of
    /// the booking, the channel commission of a portal. A share without a mandate in force is posted without commission and holds the version back.
    /// </summary>
    private async Task PlanPaymentAsync(PlanInputs inputs, PaymentSource payment, FeeBooking? feeBooking, CancellationToken ct)
    {
        var plan = inputs.Plan;
        if (payment.Gross <= 0m)
        {
            plan.Issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.SourceAmountMissing, PropertyId: payment.PropertyId, SourceId: payment.Id));
            return;
        }

        // Refunded in full, or only the tourist tax: no revenue left to share.
        var revenueTotal = OwnerLedgerRules.RevenueOfPayment(payment.Net, payment.TouristTax, payment.StayTotal);
        if (revenueTotal <= 0m)
            return;

        var paidOn = DateOnly.FromDateTime(payment.PaidAt);
        var shares = SharesOn(plan, inputs.Ownerships, payment.PropertyId, paidOn, payment.Id);
        if (shares is null)
            return;

        var currency = StayCurrency.Normalize(payment.Currency);
        var reference = payment.BookingId.ToString("N");
        var checkIn = DateOnly.FromDateTime(payment.CheckIn);
        var checkOut = DateOnly.FromDateTime(payment.CheckOut);
        var revenue = Split(revenueTotal, shares);
        var fee = feeBooking is not null && feeBooking.FirstPaymentId == payment.Id ? ChannelFeeOf(inputs, feeBooking.Stay) : null;
        var fees = fee is null ? null : Split(fee.Amount, shares);
        var cardFee = payment.ProviderFee is > 0m ? payment.ProviderFee.Value : 0m;
        var cardFees = cardFee > 0m ? Split(cardFee, shares) : null;
        for (var i = 0; i < shares.Count; i++)
        {
            var share = shares[i];
            var context = await ContextAsync(inputs.OrgId, share.OwnerId, inputs.Period, plan, inputs.Contexts, ct);
            var recognized = context.StayRecognition && checkOut > paidOn ? checkOut : paidOn;
            if (!inputs.Period.Contains(recognized))
                continue;

            var source = new Source(
                OwnerLedgerEntryType.BookingRevenue, OwnerLedgerSourceType.Payment, payment.Id, reference, "pay:" + payment.Id.ToString("N"),
                payment.PropertyId, currency, revenueTotal, checkIn, recognized, paidOn, paidOn);
            inputs.Builder.Add(source, share, revenue[i], context);

            var mandate = OwnerMandateRules.InForce(inputs.Mandates, share.OwnerId, share.OwnershipId, recognized);
            if (mandate is null)
            {
                plan.MandateMissing(share.OwnerId, payment.PropertyId, payment.BookingId);
            }
            else
            {
                AddCommission(inputs, source, shares, i, mandate, revenueTotal, payment.StayGross, payment.StayCleaning, context);

                // The card fee the provider kept (stored by the capture of E06/F04/US01/T02) is the owner's only when the mandate passes it through.
                if (mandate.PassthroughPaymentFees && cardFees is not null)
                {
                    var expense = new Source(
                        OwnerLedgerEntryType.OwnerExpense, OwnerLedgerSourceType.Payment, payment.Id, reference, "payfee:" + payment.Id.ToString("N"),
                        payment.PropertyId, StayCurrency.Normalize(payment.ProviderCurrency ?? payment.Currency), -cardFee, checkIn, recognized, paidOn, paidOn);
                    inputs.Builder.Add(expense, share, -cardFees[i], context);
                }
            }

            if (fee is not null && fees is not null)
            {
                inputs.Builder.Add(
                    source with
                    {
                        Type = OwnerLedgerEntryType.ChannelFee,
                        SourceType = OwnerLedgerSourceType.Booking,
                        SourceId = payment.BookingId,
                        Group = fee.Group,
                        SourceAmount = -fee.Amount,
                        RuleVersion = fee.Source,
                    },
                    share,
                    -fees[i],
                    context);
            }
        }
    }

    /// <summary>
    /// The lines of a stay that checks out in the month. Without a collected payment (a portal, the agency's own site, a booking entered by hand) its revenue
    /// is the gross of the booking without the tourist tax (<see cref="StaySource.Gross"/>: the <see cref="Booking.BasePrice"/>, the cleaning included, of a booking
    /// priced by CasaZen; the total of the reservation less the tourist tax for a portal) recognized on the check-out (⚖ R1), with the commission of the mandate
    /// and the channel commission of a portal. A price the host agreed by hand is an amount even when it is 0 (a stay for free): no revenue, nothing to tell.
    /// Every stay, whatever path posts its revenue, gets the flat fees of the mandate in force on the check-out, once (cleaning, linen: recharges of the agency).
    /// </summary>
    private async Task PlanStayAsync(PlanInputs inputs, StaySource stay, CancellationToken ct)
    {
        var plan = inputs.Plan;
        var reference = stay.Id.ToString("N");
        var currency = StayCurrency.Normalize(stay.Currency);
        var checkIn = DateOnly.FromDateTime(stay.CheckIn);
        var checkOut = DateOnly.FromDateTime(stay.CheckOut);
        var gross = stay.Gross;

        // A booking without its amounts (a reservation that brought none) is held back, never a revenue of zero; a price agreed by hand is an amount, 0 included.
        if (!stay.Collected && gross <= 0m && !stay.PriceOverridden)
            plan.Issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.SourceAmountMissing, PropertyId: stay.PropertyId, SourceId: stay.Id));

        // The owners of the check-out day: the shares of the revenue of a stay posted from its booking and the ones its flat fees are charged to. A property that has
        // none (or whose shares do not make 100) is told here, for a stay posted from its payments too: their revenue was shared on the day of the payment.
        if (SharesOn(plan, inputs.Ownerships, stay.PropertyId, checkOut, stay.Id) is not { } shares)
            return;

        if (!stay.Collected && gross > 0m)
        {
            var revenue = Split(gross, shares);
            var fee = ChannelFeeOf(inputs, stay);
            var fees = fee is null ? null : Split(fee.Amount, shares);
            for (var i = 0; i < shares.Count; i++)
            {
                var share = shares[i];
                var context = await ContextAsync(inputs.OrgId, share.OwnerId, inputs.Period, plan, inputs.Contexts, ct);
                var source = new Source(
                    OwnerLedgerEntryType.BookingRevenue, OwnerLedgerSourceType.Booking, stay.Id, reference, "stay:" + reference,
                    stay.PropertyId, currency, gross, checkIn, checkOut, null, checkOut);
                inputs.Builder.Add(source, share, revenue[i], context);

                var mandate = OwnerMandateRules.InForce(inputs.Mandates, share.OwnerId, share.OwnershipId, checkOut);
                if (mandate is null)
                    plan.MandateMissing(share.OwnerId, stay.PropertyId, stay.Id);
                else
                    AddCommission(inputs, source, shares, i, mandate, gross, gross, stay.CleaningFee, context);

                if (fee is not null && fees is not null)
                {
                    inputs.Builder.Add(
                        source with { Type = OwnerLedgerEntryType.ChannelFee, Group = fee.Group, SourceAmount = -fee.Amount, RuleVersion = fee.Source },
                        share,
                        -fees[i],
                        context);
                }
            }
        }

        // The flat fees of the mandate, per stay.
        for (var i = 0; i < shares.Count; i++)
        {
            var share = shares[i];
            var mandate = OwnerMandateRules.InForce(inputs.Mandates, share.OwnerId, share.OwnershipId, checkOut);
            if (mandate is null)
            {
                plan.MandateMissing(share.OwnerId, stay.PropertyId, stay.Id);
                continue;
            }

            foreach (var (kind, rate) in new[] { (OwnerLedgerFlatFees.Cleaning, mandate.CleaningFlatRate), (OwnerLedgerFlatFees.Linen, mandate.LinenFlatRate) })
            {
                if (rate is not { } amount || amount <= 0m)
                    continue;

                // A flat fee is in the settlement currency of the owner: the line needs no rate (the currency of the property while the owner is not resolved).
                var context = await ContextAsync(inputs.OrgId, share.OwnerId, inputs.Period, plan, inputs.Contexts, ct);
                var source = new Source(
                    OwnerLedgerEntryType.OwnerCharge, OwnerLedgerSourceType.MandateFlatFee, stay.Id, kind, "flat:" + reference + ":" + kind,
                    stay.PropertyId, context.Currency ?? currency, -amount, checkIn, checkOut, null, checkOut);
                inputs.Builder.Add(source, share, -Split(amount, shares)[i], context);
            }
        }
    }

    /// <summary>
    /// The commission of the agency on the share <paramref name="index"/>: the percent of the mandate on the share of its base (the whole <paramref name="amount"/>,
    /// or without its part of the cleaning), split by the shares half to even with the remainder to the last; nothing for a mandate of zero.
    /// </summary>
    private static void AddCommission(
        PlanInputs inputs,
        Source source,
        IReadOnlyList<Share> shares,
        int index,
        OwnerMandate mandate,
        decimal amount,
        decimal stayGross,
        decimal stayCleaning,
        OwnerContext context)
    {
        if (mandate.CommissionPercent <= 0m)
            return;

        var basis = OwnerLedgerRules.CommissionBaseOf(mandate.CommissionBase, amount, stayGross, stayCleaning);
        var part = Split(basis, shares)[index];
        inputs.Builder.Add(source with { Type = OwnerLedgerEntryType.AgencyCommission }, shares[index], -Round(part * mandate.CommissionPercent / 100m), context);
    }

    /// <summary>
    /// The commission a channel kept on a stay (E13/F01/US01/T02): the one of the reservation when it brought one (<c>payload</c>); otherwise, for a portal,
    /// the rule of the agency in force at the check-out (<c>channel-rule:{id}</c>) or the configured default (<c>channel-default</c>) times its base; a portal
    /// with neither is a <see cref="OwnerReadinessCodes.ChannelCommissionMissing"/> issue and no fee. <c>null</c> when there is nothing to post.
    /// </summary>
    private static ChannelFee? ChannelFeeOf(PlanInputs inputs, StaySource stay)
    {
        var reference = stay.Id.ToString("N");
        if (stay.OtaCommission is { } payload)
            return payload > 0m ? new ChannelFee(payload, ChannelFeeSources.Payload, "fee:" + reference) : null;

        if (!FiscalCopy.IsOtaBookingSource(stay.Source))
            return null;

        var terms = ChannelCommissionRules.TermsOn(inputs.ChannelRules, inputs.Defaults, stay.Source, DateOnly.FromDateTime(stay.CheckOut));
        if (terms is null)
        {
            inputs.Plan.Issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.ChannelCommissionMissing, PropertyId: stay.PropertyId, SourceId: stay.Id));
            return null;
        }

        var gross = stay.Gross;
        var basis = OwnerLedgerRules.CommissionBaseOf(terms.Base, gross, gross, stay.CleaningFee);
        var amount = Round(basis * terms.Percent / 100m);
        var group = "fee:" + reference + (terms.RuleId is { } ruleId ? ":rule:" + ruleId.ToString("N") : ":default");
        return amount > 0m ? new ChannelFee(amount, terms.Source, group) : null;
    }

    /// <summary>
    /// The bookings among <paramref name="bookingIds"/> whose revenue the booking path posted in another month and that is still there (not reversed): the stay
    /// is recognized there, so its payments do not post it again.
    /// </summary>
    private async Task<HashSet<Guid>> StaysPostedFromBookingElsewhereAsync(Guid orgId, Period period, List<Guid> bookingIds, CancellationToken ct)
    {
        if (bookingIds.Count == 0)
            return [];

        var lines = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.SourceType == OwnerLedgerSourceType.Booking && e.EntryType == OwnerLedgerEntryType.BookingRevenue
                && e.ReversesEntryId == null && e.SourceId != null && bookingIds.Contains(e.SourceId.Value)
                && (e.PeriodYear != period.Year || e.PeriodMonth != period.Month))
            .Select(e => new { e.Id, BookingId = e.SourceId!.Value })
            .ToListAsync(ct);
        var reversed = await ReversedIdsAsync(orgId, lines.Select(e => e.Id).ToList(), ct);
        return lines.Where(e => !reversed.Contains(e.Id)).Select(e => e.BookingId).ToHashSet();
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
        var active = ActiveOwnerships(ownerships, propertyId, day);
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

    private static List<PropertyOwnership> ActiveOwnerships(IReadOnlyList<PropertyOwnership> ownerships, Guid propertyId, DateOnly day) =>
        ownerships
            .Where(o => o.PropertyId == propertyId && DateOnly.FromDateTime(o.OwnershipStart) <= day
                && (o.OwnershipEnd == null || DateOnly.FromDateTime(o.OwnershipEnd.Value) > day))
            .OrderBy(o => o.PropertyOwnerId)
            .ToList();

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

        /// <summary>The (owner, stay) already told to have no mandate: one issue each.</summary>
        private HashSet<(Guid Owner, Guid Stay)> MissingMandates { get; } = [];

        /// <summary>
        /// A stay of an owner with no mandate in force on its day (E13/F01/US01/T02): one blocking <see cref="OwnerReadinessCodes.MandateMissingForStay"/> per
        /// owner and stay, instead of a commission of zero.
        /// </summary>
        public void MandateMissing(Guid ownerId, Guid propertyId, Guid stayId)
        {
            if (MissingMandates.Add((ownerId, stayId)))
                Issues.Add(new OwnerReadinessIssue(OwnerReadinessCodes.MandateMissingForStay, ownerId, propertyId, stayId));
        }
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

                // A channel commission says where it comes from (payload, channel-rule:{id}, channel-default); every other line the rule set of the owner.
                RuleVersion = source.RuleVersion ?? context.RuleVersion,
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
        string? Attachment = null,
        string? RuleVersion = null);

    /// <summary>A collected payment with the facts of its stay the posting needs (the tourist tax, the total, the gross and the cleaning) and the card fee.</summary>
    private sealed record PaymentSource(
        Guid Id,
        Guid BookingId,
        Guid PropertyId,
        decimal Gross,
        decimal Net,
        DateTime PaidAt,
        DateTime CheckIn,
        DateTime CheckOut,
        string? Currency,
        decimal TouristTax,
        decimal StayTotal,
        decimal StayGross,
        decimal StayCleaning,
        decimal? ProviderFee,
        string? ProviderCurrency);

    /// <summary>
    /// An active stay: its amounts (<see cref="Booking.BasePrice"/>, the gross without the tourist tax of a booking priced by CasaZen, with the cleaning in it; the
    /// total of the reservation and its tourist tax, which a portal stay is grossed from), the commission its reservation brought, its days, whether a payment of
    /// it was collected (then the payment path posts its revenue) and whether the host agreed its price by hand (<see cref="Booking.PriceOverridden"/>).
    /// </summary>
    private sealed record StaySource(
        Guid Id,
        Guid PropertyId,
        BookingSource Source,
        decimal BasePrice,
        decimal CleaningFee,
        decimal? OtaCommission,
        DateTime CheckIn,
        DateTime CheckOut,
        string? Currency,
        bool Collected,
        decimal TotalPrice,
        decimal TouristTax,
        bool PriceOverridden)
    {
        /// <summary>The gross of the stay without the tourist tax (<see cref="OwnerLedgerRules.GrossOfStay"/>): what its booking posts and what the commissions are a percentage of.</summary>
        public decimal Gross => OwnerLedgerRules.GrossOfStay(FiscalCopy.IsOtaBookingSource(Source), BasePrice, TotalPrice, TouristTax);
    }

    /// <summary>A booking whose channel commission goes with its first payment that still holds money.</summary>
    private sealed record FeeBooking(StaySource Stay, Guid FirstPaymentId);

    /// <summary>A channel commission to post: the amount, where it comes from (the rule version of its line) and the group of its key.</summary>
    private sealed record ChannelFee(decimal Amount, string Source, string Group);

    /// <summary>What the lines of a month are computed from, read once by <see cref="PlanAsync"/>.</summary>
    private sealed record PlanInputs(
        Guid OrgId,
        Period Period,
        Plan Plan,
        PlanBuilder Builder,
        Dictionary<Guid, OwnerContext> Contexts,
        IReadOnlyList<PropertyOwnership> Ownerships,
        IReadOnlyList<OwnerMandate> Mandates,
        IReadOnlyList<OrgChannelCommissionRule> ChannelRules,
        OwnerAccountingOptions Defaults);

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
