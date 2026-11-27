using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The collections report of the host (NF-97, E07/F03/US02/T01): the money that came in by date, by payment method and by channel, the balances still to
/// collect and the credits. Money, so the web layer reads it only for a caller with <c>payment.read</c>, and it shows only what the caller's
/// <see cref="HostScope"/> reaches, in SQL (<c>InScope</c>). Each read is a fixed number of queries whatever the number of payments.
/// </summary>
public interface IHostCollectionsReportService
{
    /// <summary>
    /// The money collected in <paramref name="query"/> (both days included, Europe/Rome), by cash: a payment counts when
    /// <see cref="PaymentCashRules.CollectedBetweenInstants"/> says so, for what it brings (<see cref="PaymentCashRules.Collected"/>), on the Europe/Rome day of
    /// the instant it came in. The total is the Home's "incassato" for the same days (<see cref="HostDashboardCollected"/>). With the credits of the card
    /// provider and of the channels (<see cref="HostCredits"/>), from what the booking-payments work and the booking import stored: nothing is captured or
    /// asked of a provider here, and what is not stored is "not available", never zero.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The property of the query is not one the scope reaches (one 404 for a missing, another org's or unreached one).</exception>
    Task<HostCollectionsReport> GetCollectionsAsync(HostScope scope, HostReportQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The stays that still have something to collect, as of today (Europe/Rome): not cancelled, not ended before <see cref="HostCollectionsRules.BalancesSince"/> (24 months back)
    /// and not holding their dates through an expired checkout hold
    /// (<see cref="CheckoutHolds.OccupiesDates"/>), whose amount due is above what was collected. A stay whose money is CasaZen's (<see cref="HostCollectionsRules.HoldsCasaZenMoney"/>)
    /// owes its total minus the payments collected for it; one whose money is the channel manager's, the balance stored from the reservation, and nothing when the
    /// channel collects it, when it does not say who collects (the reading of the payment ledger) or when the balance was never reported. No guest data in any row. Sorted by check-in.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The property of the query is not one the scope reaches.</exception>
    Task<HostBalancesToCollect> GetBalancesToCollectAsync(HostScope scope, HostBalancesQuery query, CancellationToken cancellationToken = default);
}
