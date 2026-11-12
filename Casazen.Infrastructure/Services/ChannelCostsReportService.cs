using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IChannelCostsReportService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>The Home's stays.</b> The rows are the confirmed stays with a night in the period (<see cref="StayKpiRules.IsConfirmedStay"/>,
/// <see cref="PropertyOccupancy.BookingTakesNightIn(DateTime, DateTime)"/>): the very ones of <c>bookings-by-channel</c> and of the revenue of the Home, so the gross
/// of a channel is that report's revenue and the total is the Home's.</item>
/// <item><b>What each stay costs, as reported.</b> A stay whose money is the channel manager's (<see cref="BookingAmountSourceRules.HoldsChannelManagerMoney"/>: a
/// booking of a channel with its reservation, or a reservation of the booking engine of the agency) costs the commission the import stored
/// (<c>OtaCommission</c>), null when Octorate gave none; a stay of a channel with no reservation (an iCal stay) was never reported one, so it is unknown too; a
/// direct stay costs the fees of its settled card payments (<c>ProviderFeeAmount</c>, in the currency of the stay), and its cost is unknown (null, counted apart)
/// as soon as one of them has no stored fee yet, while a card payment is still on its way (Pending, Processing), while the card saved for a deferred charge was
/// not charged yet, and when nothing was collected at all (not paid yet, or a failed attempt only): it is nothing (0) only when what was collected came without a
/// card (cash, bank transfer); a stay the host entered, or a local one, costs nothing. Nothing is derived from another figure.</item>
/// <item><b>Reach in SQL, fixed number of queries.</b> Both reads narrow with <c>InScope</c>; a property outside the scope is one 404. The stays, the payments of
/// the direct stays and the cached portal settings (<see cref="IChannelCommissionSettingsProvider"/>): a few statements whatever the number of stays.</item>
/// </list>
/// </remarks>
public sealed class ChannelCostsReportService(
    AppDbContext db,
    IChannelCommissionSettingsProvider settings) : IChannelCostsReportService
{
    public async Task<ChannelCostsReport> GetChannelCostsAsync(
        HostScope scope,
        HostReportQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var period = HostReportRules.PeriodOf(query.From, query.To);
        await EnsurePropertyInScopeAsync(scope, query.PropertyId, cancellationToken);

        var stays = await RevenueStays(scope, query.PropertyId, period)
            .Select(b => new StayRow(
                b.Id,
                b.Source,
                b.BasePrice,
                b.CheckInDate,
                b.CheckOutDate,
                b.OctorateReservationId,
                b.OtaCommission,
                b.OtaNetAmount,
                db.OctorateBookingSyncs.Any(s => s.BookingId == b.Id),
                b.PaymentOption,
                b.StripeSetupIntentId != null))
            .ToListAsync(cancellationToken);

        // The payments of the direct stays that say something of their cost (a failed or canceled one costs nothing and collected nothing), with the fee the capture
        // stored for the card ones: the cost of a direct stay (read through the payment ledger's own columns).
        var payments = await RevenueStays(scope, query.PropertyId, period)
            .Where(b => b.Source == BookingSource.Direct)
            .SelectMany(
                b => b.Payments.Where(p => p.Status != PaymentStatus.Failed && p.Status != PaymentStatus.Canceled),
                (b, p) => new PaymentRow(b.Id, p.Status, p.StripePaymentIntentId != null, p.ProviderFeeAmount, p.ProviderBalanceCurrency, b.Property.Currency))
            .ToListAsync(cancellationToken);

        var modes = await settings.GetModesAsync(scope.OrgId, cancellationToken);

        var paymentsOf = payments.ToLookup(p => p.BookingId);
        var costStays = stays.Select(s => CostStayOf(s, paymentsOf[s.Id])).ToList();
        return ChannelCostRules.Report(period, costStays, modes);
    }

    private static ChannelCostStay CostStayOf(StayRow stay, IEnumerable<PaymentRow> payments)
    {
        // Whose money the stay is decides where its cost is read: the one definition of NF-77, not a test of the source written again.
        var facts = new Booking { Source = stay.Source, OctorateReservationId = stay.OctorateReservationId };
        if (BookingAmountSourceRules.HoldsChannelManagerMoney(facts, stay.HasMirror))
            return new ChannelCostStay(stay.Source, stay.BasePrice, stay.CheckIn, stay.CheckOut, stay.OtaCommission, stay.OtaNetAmount);

        return new ChannelCostStay(stay.Source, stay.BasePrice, stay.CheckIn, stay.CheckOut, CostOf(stay, payments), null);
    }

    private static decimal? CostOf(StayRow stay, IEnumerable<PaymentRow> payments)
    {
        switch (ChannelCostRules.KindOf(stay.Source))
        {
            case ChannelCostKind.ChannelCommission:
                // A stay of a channel with no reservation on Octorate (an iCal stay): no commission was ever reported for it.
                return null;

            case ChannelCostKind.NoCost:
                return 0m;

            default:
                return DirectCostOf(stay, payments.ToList());
        }
    }

    // What a stay of the booking site costs: the fees of its settled card payments, and only what is known is a cost. Zero is a cost only for a stay that was collected
    // without a card; a stay nobody paid yet, one whose card is still on its way or is charged later is not free, it is not known (the rate of the row does not count it).
    private static decimal? DirectCostOf(StayRow stay, IReadOnlyCollection<PaymentRow> payments)
    {
        // A card payment still on its way will cost its fee once it settles.
        if (payments.Any(p => p.IsCard && p.Status is PaymentStatus.Pending or PaymentStatus.Processing))
            return null;

        var settled = payments.Where(p => p.IsCard && PaymentProviderFeeService.SettledStatuses.Contains(p.Status)).ToList();
        if (settled.Count == 0)
        {
            // The card saved for the deferred charge was not charged yet: its fee is still to come.
            if (stay.PaymentOption == PaymentOption.OnCancellationDeadline && stay.HasSavedCard)
                return null;

            // Cash or a bank transfer has no fee, but only money that came in says so: nothing collected is nothing known.
            return payments.Any(p => !p.IsCard && p.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded) ? 0m : null;
        }

        var total = 0m;
        foreach (var payment in settled)
        {
            // A fee in another currency than the stay's cannot be added to it, and one not stored yet is not a fee of zero: either way the cost is not known.
            if (payment.Fee is not { } fee
                || !string.Equals(StayCurrency.Normalize(payment.BalanceCurrency), StayCurrency.Normalize(payment.PropertyCurrency), StringComparison.Ordinal))
            {
                return null;
            }

            total += fee;
        }

        return total;
    }

    // The rows the revenue is made of, defined once by the code of the Home: the confirmed stays that have a night in the period.
    private IQueryable<Booking> RevenueStays(HostScope scope, Guid? propertyId, HostDashboardPeriod period)
    {
        var bookings = db.Bookings.AsNoTracking().Where(b => b.OrgId == scope.OrgId).InScope(scope);
        if (propertyId is { } id)
            bookings = bookings.Where(b => b.PropertyId == id);

        return bookings
            .Where(StayKpiRules.IsConfirmedStay())
            .Where(PropertyOccupancy.BookingTakesNightIn(period.From, period.To));
    }

    // One 404 for a property that is missing, another org's or not reached by the caller: nothing tells them apart.
    private async Task EnsurePropertyInScopeAsync(HostScope scope, Guid? propertyId, CancellationToken cancellationToken)
    {
        if (propertyId is not { } id)
            return;

        var reachable = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId && p.Id == id)
            .InScope(scope)
            .AnyAsync(cancellationToken);
        if (!reachable)
            throw new NotFoundException($"Property {id} not found") { MessageKey = "PropertyNotFound" };
    }

    private sealed record StayRow(
        Guid Id,
        BookingSource Source,
        decimal BasePrice,
        DateTime CheckIn,
        DateTime CheckOut,
        string? OctorateReservationId,
        decimal? OtaCommission,
        decimal? OtaNetAmount,
        bool HasMirror,
        PaymentOption PaymentOption,
        bool HasSavedCard);

    private sealed record PaymentRow(Guid BookingId, PaymentStatus Status, bool IsCard, decimal? Fee, string? BalanceCurrency, string? PropertyCurrency);
}
