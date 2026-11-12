using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The channel costs report of the host (NF-98, E07/F03/US02/T02): gross, commission and net for each channel, and the processing fees of the direct stays, with
/// how each channel settles its commission. Money, so the web layer reads it only for a caller with <c>payment.read</c>, and it shows only what the caller's
/// <see cref="HostScope"/> reaches, in SQL. By accrual, like the revenue of the Home: the stays are exactly the ones of <c>bookings-by-channel</c> (the confirmed
/// stays with a night in the period) and the gross is the Home's revenue of them.
/// </summary>
public interface IChannelCostsReportService
{
    /// <summary>
    /// What each channel costs in <paramref name="query"/> (both days included, Europe/Rome). The commission and the net come from what the booking import stored
    /// (<c>Booking.OtaCommission</c>, <c>OtaNetAmount</c>), pro rata per night in the period as the revenue is; the costs of the direct stays are the fees the card
    /// provider reported (<c>Payment.ProviderFeeAmount</c>); how the commission of a channel is settled comes from the cached portal settings. Nothing is captured, read
    /// from Octorate or sent here, and a cost nobody reported is not available (null), never zero: the stays without one are counted apart.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The property of the query is not one the scope reaches (one 404 for a missing, another org's or unreached one).</exception>
    Task<ChannelCostsReport> GetChannelCostsAsync(HostScope scope, HostReportQuery query, CancellationToken cancellationToken = default);
}
