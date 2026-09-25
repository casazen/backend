namespace Casazen.Core.Services;

/// <summary>
/// Alerts sent by the recurring jobs (stay alerts: Alloggiati stages and check-out reminder, CO-10; CIN deadline). The
/// booking emails (BK-10) are sent by <c>BookingNotifier</c>, the refund emails by the refund service: no stub methods
/// here.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Delivers a stay alert of the <c>stay-alerts</c> job (CO-10) to the property's hosts: an email to the org's contact
    /// address, queued on Hangfire, and a push. The job decides when; this only renders and delivers.
    /// </summary>
    Task SendStayAlertAsync(StayAlert alert, CancellationToken cancellationToken = default);

    Task SendCinDeadlineAlertAsync(string ownerId, IReadOnlyList<Guid> propertyIds, int daysUntilDeadline);

    /// <summary>
    /// An OTA stay created from an iCal block became "da verificare" (CO-21): a sync found its block gone from the feed or
    /// with other dates. An email to the org's contact address, queued on Hangfire, and a push to the property's hosts. The
    /// sync decides when (once per change it finds); this only renders and delivers.
    /// </summary>
    Task SendOtaStayReviewAlertAsync(OtaStayReviewAlert alert, CancellationToken cancellationToken = default);
}

/// <summary>
/// An OTA stay to check (CO-21): <paramref name="ChannelCheckIn"/> and <paramref name="ChannelCheckOut"/> are the dates
/// the channel now shows, for <see cref="Entities.Enums.OtaStayReviewReason.BlockDatesChanged"/>.
/// </summary>
public sealed record OtaStayReviewAlert(
    Guid BookingId,
    Entities.Enums.OtaStayReviewReason Reason,
    DateTime? ChannelCheckIn = null,
    DateTime? ChannelCheckOut = null);
