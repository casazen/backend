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
    /// address and a push to their devices, both queued on Hangfire (the push once per stage and device, MO-04). The job
    /// decides when; this only renders and queues.
    /// </summary>
    Task SendStayAlertAsync(StayAlert alert, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers a CIN alert of the <c>cin-deadline-alert</c> job (CO-20) to an org: one email to its contact address,
    /// queued on Hangfire, listing its properties without a valid CIN. The job decides when; this only renders and
    /// delivers. False when the email was not queued (no contact address, provider not configured), with a log entry.
    /// </summary>
    Task<bool> SendCinDeadlineAlertAsync(CinDeadlineAlert alert, CancellationToken cancellationToken = default);

    /// <summary>
    /// An OTA stay created from an iCal block became "da verificare" (CO-21): a sync found its block gone from the feed or
    /// with other dates. An email to the org's contact address, queued on Hangfire, and a push to the property's hosts. The
    /// sync decides when (once per change it finds); this only renders and delivers.
    /// </summary>
    Task SendOtaStayReviewAlertAsync(OtaStayReviewAlert alert, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells the host about a scheduled change of rental mode (PM-02): it was programmed, applied, or could not be applied.
    /// One email to the org's contact address, queued on Hangfire. The mode change service decides when (once per transition
    /// of the change); this only renders and delivers. False when the email was not queued (no contact address, provider
    /// not configured, change not found), with a log entry: the change itself is never undone by a failed notice.
    /// </summary>
    Task<bool> SendPropertyModeChangeAsync(PropertyModeNotice notice, CancellationToken cancellationToken = default);
}

/// <summary>Which moment of a scheduled change of rental mode a <see cref="PropertyModeNotice"/> is about.</summary>
public enum PropertyModeNoticeKind
{
    /// <summary>The change was programmed (to confirm it and say how to withdraw it).</summary>
    Scheduled = 0,

    /// <summary>The property changed mode, on its day.</summary>
    Applied = 1,

    /// <summary>The change could not be applied on its day; the property kept its mode.</summary>
    Failed = 2,
}

/// <summary>
/// A scheduled change of rental mode to tell the host about (PM-02). <paramref name="EarliestDate"/> is, for a
/// <see cref="PropertyModeNoticeKind.Failed"/> change that was blocked by stays, blocks or leases, the first day that is free
/// now (so the e-mail can suggest it); <c>null</c> otherwise.
/// </summary>
public sealed record PropertyModeNotice(
    Guid ChangeId,
    PropertyModeNoticeKind Kind,
    DateTime? EarliestDate = null);

/// <summary>
/// An OTA stay to check (CO-21): <paramref name="ChannelCheckIn"/> and <paramref name="ChannelCheckOut"/> are the dates
/// the channel now shows, for <see cref="Entities.Enums.OtaStayReviewReason.BlockDatesChanged"/>.
/// </summary>
public sealed record OtaStayReviewAlert(
    Guid BookingId,
    Entities.Enums.OtaStayReviewReason Reason,
    DateTime? ChannelCheckIn = null,
    DateTime? ChannelCheckOut = null);
