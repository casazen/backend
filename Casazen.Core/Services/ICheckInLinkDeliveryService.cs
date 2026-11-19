using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The delivery of the check-in link of a booking that has no e-mail address (E23/F01/US01/T01, T02, NF-122, NF-63, decision O10): a stay from a portal comes with an
/// empty address, so the link of the daily job <c>guest-checkin-send</c> used to fail in silence (<c>no_recipient</c>). This service tries the ways the guest can be
/// reached without the host, in this order, behind their own flags, and records where the link stands so that the host sees why it has not arrived:
/// <list type="number">
/// <item><b>An address the booking has</b> (the host wrote it, or an earlier read gave it): the e-mail, as for any booking.</item>
/// <item><b>The reservation thread of the portal chat</b> (<see cref="Features.FeatureFlags.PortalChatCheckInLink"/>): ONE message, written by CasaZen as an
/// <see cref="MessageOrigin.Automation"/> message through <see cref="IMessageReplyService"/>, with the same rules as any reply (the mapping decided, the rules of the
/// channel, one send for one key), in the language of the guest. A message never goes twice for one link: its key is the booking and the token.</item>
/// <item><b>The contact of the guest</b> (<see cref="Features.FeatureFlags.GuestContactInfo"/>, <see cref="IGuestContactInfoService"/>): the e-mail read once from
/// the channel manager, for this link only; then the e-mail.</item>
/// <item><b>The host</b>: when none of them can be used the row says what the link waits for, a «To do» whose «Copia link» lets the host carry the link, and, when the
/// automatic delivery failed, the bell and a pop-up.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>The flags.</b> With both new flags off the service writes the row with the state <see cref="CheckInLinkDeliveryState.WaitingContact"/> and the channel
/// <see cref="CheckInLinkChannel.Manual"/> and nothing else: no call, no «To do», no notification, no token stored, and the caller does what it always did.</para>
/// <para><b>Nothing is swallowed.</b> A refusal or a failure of the channel manager is recorded with its stable code (<see cref="CheckInLinkDelivery.LastErrorCode"/>);
/// the ones that are worth a retry (a limit, an outage) are tried again by the hourly job <c>checkin-link-delivery-retry</c> up to
/// <see cref="CheckInLinkDeliveryRules.MaxAttempts"/> times. A message whose outcome nobody knows is never sent again (OCT-10).</para>
/// <para><b>Personal data.</b> No address, number, name or word of a message is ever logged: ids, states and codes.</para>
/// </remarks>
public interface ICheckInLinkDeliveryService
{
    /// <summary>
    /// First delivery of <paramref name="link"/>, just issued for the booking: writes (or starts again) the row of the booking and makes the first attempt. Never throws
    /// for what the channel manager answers: the answer is the state of the row. The row of a booking whose link is already being delivered (the same session) is
    /// returned as it is. A booking that does not exist is a <see cref="Exceptions.NotFoundException"/>.
    /// </summary>
    Task<CheckInLinkDeliveryView> DeliverAsync(Guid bookingId, IssuedCheckInLink link, CancellationToken ct = default);

    /// <summary>
    /// The link is about to go to the guest inside an e-mail the caller queues itself: the confirmation of a direct booking (E23/F02/US01/T01). Writes (or starts again) the row of the
    /// booking as <see cref="CheckInLinkDeliveryState.Sending"/> («in attesa»: the e-mail is not with the provider yet) by <see cref="CheckInLinkChannel.Email"/> for
    /// <paramref name="link"/>, with no token (the link is in the e-mail and nobody has to carry it), and marks the check-in session of the link as <c>Queued</c>. <b>Never «sent»</b>:
    /// only <see cref="RecordEmailSentAsync"/>, once the provider accepted the e-mail, says so (CO-09, A5-26). <b>No e-mail of its own is queued</b>: the confirmation is the
    /// delivery, so the daily job finds a usable link and sends nothing more. A booking that does not exist is a <see cref="Exceptions.NotFoundException"/>.
    /// </summary>
    Task RecordEmailQueuedAsync(Guid bookingId, IssuedCheckInLink link, CancellationToken ct = default);

    /// <summary>
    /// The provider accepted the e-mail that carries the link of <paramref name="sessionId"/>: the session says <c>Sent</c> with the time, as the e-mail of the link does, and the row
    /// of the booking, if it is still the one of this link, <see cref="CheckInLinkDeliveryState.Sent"/> (and its «To do» to carry the link, if any, is closed). Idempotent; a session that is
    /// not the booking's changes nothing.
    /// </summary>
    Task RecordEmailSentAsync(Guid bookingId, Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// The e-mail that carries the link of <paramref name="sessionId"/> did not reach the provider (refused, not delivered after the attempts, not queued: <paramref name="errorCode"/>, one
    /// of <see cref="Entities.GuestCheckInLinkEmailErrors"/>): the session and the row say <c>Failed</c> with the code, and the link is <b>taken back</b>, its session expires, because nobody
    /// has it (only its hash is stored) and a usable link would stop the daily job from sending one. A link the guest already opened is left alone: the e-mail did reach them.
    /// </summary>
    Task RecordEmailFailedAsync(Guid bookingId, Guid sessionId, string errorCode, CancellationToken ct = default);

    /// <summary>
    /// The hourly pass: tries again the rows that wait for something that may have changed (the chat of a listing that reads again, a thread that arrived, a day after
    /// a refusal of the contact, a failure worth a retry), and ends the rows whose link is no longer useful. Does nothing while both new flags are off. One row's failure
    /// never stops the others (the first one is thrown when the pass is over). Returns how many rows it tried again.
    /// </summary>
    /// <param name="issueLink">
    /// Issues a new link for (booking, org): called only for a row made while the flags were off, which holds no token because nobody ever had its link. The caller passes
    /// <see cref="IGuestCheckInService.IssueLinkAsync"/>; it is a parameter so that this service does not depend on the check-in service, which tells it when a guest completes the check-in.
    /// </param>
    Task<int> RetryWaitingAsync(Func<Guid, Guid, Task<IssuedCheckInLink>> issueLink, CancellationToken ct = default);

    /// <summary>
    /// The delivery of a booking of <paramref name="orgId"/> as the host sees it, or <c>null</c> when the booking has none. <paramref name="includeLink"/> is true only
    /// for a caller who can already copy the link (<c>booking.write</c> on the property): then <see cref="CheckInLinkDeliveryView.Link"/> holds it while it can still be
    /// used. The caller checked the booking was within its reach.
    /// </summary>
    Task<CheckInLinkDeliveryView?> GetAsync(Guid orgId, Guid bookingId, bool includeLink, CancellationToken ct = default);

    /// <summary>
    /// Ends the row of a booking of <paramref name="orgId"/> for <paramref name="reason"/> (<see cref="CheckInLinkEndReasons"/>), closes its «To do» and clears the stored
    /// token. Idempotent: no row, or a row that ended, changes nothing. Best effort for the callers that are not about the link itself: it never throws for what it finds.
    /// </summary>
    Task EndAsync(Guid orgId, Guid bookingId, string reason, CancellationToken ct = default);
}

/// <summary>
/// The delivery of a link as the host sees it (<c>GET api/bookings/{id}/checkin/link-delivery</c>). <paramref name="Link"/> is the link to copy, only for a caller who
/// can already copy one and only while the delivery waits or failed and the link can still be opened; <c>null</c> otherwise. <paramref name="ExpiresAt"/> is the end of
/// validity of the link.
/// </summary>
public sealed record CheckInLinkDeliveryView(
    CheckInLinkChannel Channel,
    CheckInLinkDeliveryState State,
    int Attempts,
    string? LastErrorCode,
    DateTime? SentAt,
    DateTime? EndedAt,
    string? EndedReason,
    DateTime? ExpiresAt,
    string? Link)
{
    /// <summary>True when the service took the link over (a channel other than the host's hands): the caller then does not queue the e-mail itself.</summary>
    public bool IsAutomatic => Channel != CheckInLinkChannel.Manual;
}

/// <summary>Why a row of the delivery ended (<see cref="Entities.CheckInLinkDelivery.EndedReason"/>): codes, never a text.</summary>
public static class CheckInLinkEndReasons
{
    /// <summary>The guest completed the check-in.</summary>
    public const string CheckInCompleted = "check_in_completed";

    /// <summary>The booking is no longer one that takes a check-in: cancelled, a no-show, checked out, or not confirmed.</summary>
    public const string BookingNotActive = "booking_not_active";

    /// <summary>The link expired and nobody issued another.</summary>
    public const string LinkExpired = "link_expired";

    /// <summary>Another link of the booking replaced this one.</summary>
    public const string LinkReplaced = "link_replaced";

    /// <summary>The host generated or sent a link itself: it carries the link from here.</summary>
    public const string HostLink = "host_link";
}

/// <summary>The rules of the delivery that need no database, in one place (E23/F01/US01/T01, T02).</summary>
public static class CheckInLinkDeliveryRules
{
    /// <summary>The least time between two automatic attempts of one booking that waits for the chat or that failed for a reason worth a retry.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    /// <summary>The least time between two reads of the contact of a guest after a refusal: no retry storm against a channel manager that says no (T02).</summary>
    public static readonly TimeSpan ContactRetryInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// The least time between two sends to the chat of a booking whose last send the channel manager refused for the chat permission: the copy of the conversations reads the listing fine and
    /// moves its state at every pass, so it says nothing about the permission to send. One refused message a day, never one an hour (each leaves a message in the state error in the thread).
    /// </summary>
    public static readonly TimeSpan RefusedSendRetryInterval = TimeSpan.FromDays(1);

    /// <summary>The attempts of an automatic delivery that failed for a reason worth a retry (a limit, an outage), the first one included.</summary>
    public const int MaxAttempts = 3;

    /// <summary>The code of a message of the chat whose outcome nobody knows: it may have left, so it is never sent again (OCT-10).</summary>
    public const string ManualVerifyCode = "manual_verify";

    /// <summary>The listing of the booking has no confirmed mapping: nothing is written to a channel for it (OCT-04).</summary>
    public const string NotMappedCode = "mapping_not_confirmed";

    /// <summary>A waiting state: the link waits for something that is not the host's to do (the permission, the conversation, a way to the guest, the IP addresses).</summary>
    public static bool IsWaiting(CheckInLinkDeliveryState state) =>
        state is CheckInLinkDeliveryState.WaitingChatPermission
            or CheckInLinkDeliveryState.WaitingThread
            or CheckInLinkDeliveryState.WaitingContact
            or CheckInLinkDeliveryState.WaitingStaticIps;

    /// <summary>The link has not reached the guest and the host may have to carry it: a waiting state or a failure. It is what the «To do» «Copia link» stands for.</summary>
    public static bool NeedsTheHost(CheckInLinkDeliveryState state) => IsWaiting(state) || state == CheckInLinkDeliveryState.Failed;

    /// <summary>
    /// The key of the one message of the chat that carries a link (<c>Idempotency-Key</c> of the reply): the booking and the token, so the same link is never written
    /// twice in a thread, whatever the number of attempts or of runs, and a new link is a new message. A hash: the token itself is never in a key (the keys are stored and
    /// read by the screens). 44 characters, letters, digits and a dash.
    /// </summary>
    public static string IdempotencyKey(Guid bookingId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{bookingId:N}:{token}"));
        return "cil-" + Convert.ToHexStringLower(hash)[..40];
    }
}
