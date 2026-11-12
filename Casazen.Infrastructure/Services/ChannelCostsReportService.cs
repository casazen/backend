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
/// direct stay costs the fees of its card payments (<c>ProviderFeeAmount</c>, in the currency of the stay), unknown as soon as one of its settled card payments has no
/// stored fee yet, and nothing when it has no card payment; a stay the host entered, or a local one, costs nothing. Nothing is derived from another figure.</item>
/// <item><b>Reach in SQL, fixed number of queries.</b> Both reads narrow with <c>InScope</c>; a property outside the scope is one 404. The stays, the card payments of
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
                db.OctorateBookingSyncs.Any(s => s.BookingId == b.Id)))
            .ToListAsync(cancellationToken);

        // The settled card payments of the direct stays, with the fee the capture stored: the cost of a direct stay (read through the payment ledger's own columns).
        var cardPayments = await RevenueStays(scope, query.PropertyId, period)
            .Where(b => b.Source == BookingSource.Direct)
            .SelectMany(
                b => b.Payments.Where(p => p.StripePaymentIntentId != null && PaymentProviderFeeService.SettledStatuses.Contains(p.Status)),
                (b, p) => new CardPaymentRow(b.Id, p.ProviderFeeAmount, p.ProviderBalanceCurrency, b.Property.Currency))
            .ToListAsync(cancellationToken);

        var modes = await settings.GetModesAsync(scope.OrgId, cancellationToken);

        var paymentsOf = cardPayments.ToLookup(p => p.BookingId);
        var costStays = stays.Select(s => CostStayOf(s, paymentsOf[s.Id])).ToList();
        return ChannelCostRules.Report(period, costStays, modes);
    }

    private static ChannelCostStay CostStayOf(StayRow stay, IEnumerable<CardPaymentRow> cardPayments)
    {
        // Whose money the stay is decides where its cost is read: the one definition of NF-77, not a test of the source written again.
        var facts = new Booking { Source = stay.Source, OctorateReservationId = stay.OctorateReservationId };
        if (BookingAmountSourceRules.HoldsChannelManagerMoney(facts, stay.HasMirror))
            return new ChannelCostStay(stay.Source, stay.BasePrice, stay.CheckIn, stay.CheckOut, stay.OtaCommission, stay.OtaNetAmount);

        return new ChannelCostStay(stay.Source, stay.BasePrice, stay.CheckIn, stay.CheckOut, CostOf(stay.Source, cardPayments), null);
    }

    private static decimal? CostOf(BookingSource source, IEnumerable<CardPaymentRow> cardPayments)
    {
        switch (ChannelCostRules.KindOf(source))
        {
            case ChannelCostKind.ChannelCommission:
                // A stay of a channel with no reservation on Octorate (an iCal stay): no commission was ever reported for it.
                return null;

            case ChannelCostKind.NoCost:
                return 0m;

            default:
                var total = 0m;
                foreach (var payment in cardPayments)
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
        bool HasMirror);

    private sealed record CardPaymentRow(Guid BookingId, decimal? Fee, string? BalanceCurrency, string? PropertyCurrency);
}
