using Casazen.Core.Entities;

namespace Casazen.Core.GuestPortal;

/// <summary>
/// The e-mails the host sends to the guest of a stay about its stay link (E50/F01/US03/T03, NF-152, section 5.1): «Prepara il tuo arrivo» again, on the host's request. The address is always
/// the one of the booking's guest, never one the caller gives; the link in it is made by <see cref="IGuestPortalLinkService"/> and nowhere else. Like every host method of the portal it
/// names the org, which the web layer took from the booking the caller may write (tenant and HostScope): services never check roles.
/// </summary>
public interface IGuestPortalEmailService
{
    /// <summary>
    /// Queues «Prepara il tuo arrivo» to the e-mail address of the booking's guest, with the stay link (made now if the booking has none) as its main button, in the guest's language and
    /// with the brand of the host. It carries no check-in link: the one the guest holds stays valid (a new one would replace it). The outcome says why nothing was queued; nothing is
    /// queued, and no link made, for a booking with no usable guest address or that may not have a link. A booking that is not in the org is a
    /// <see cref="Exceptions.NotFoundException"/>. The logs carry ids, never the address or the token.
    /// </summary>
    /// <param name="userId">The user (Auth0 subject) who asked, for the link when this call makes it.</param>
    Task<GuestPortalResendResult> ResendPrepareArrivalAsync(
        Guid orgId,
        Guid bookingId,
        string? userId,
        CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IGuestPortalEmailService.ResendPrepareArrivalAsync"/> did.</summary>
/// <param name="Outcome">Whether the e-mail was queued and, when it was not, why.</param>
/// <param name="Link">The active stay link of the booking, when the e-mail was queued.</param>
public sealed record GuestPortalResendResult(GuestPortalResendOutcome Outcome, GuestPortalLink? Link = null)
{
    public static GuestPortalResendResult Queued(GuestPortalLink link) => new(GuestPortalResendOutcome.Queued, link);

    public static GuestPortalResendResult Refused(GuestPortalResendOutcome outcome) => new(outcome);
}

/// <summary>The answer of <see cref="IGuestPortalEmailService.ResendPrepareArrivalAsync"/>.</summary>
public enum GuestPortalResendOutcome
{
    /// <summary>The e-mail is with the queue of the e-mails.</summary>
    Queued,

    /// <summary>The guest has no address to write to (an OTA booking without one, an erased guest, a guest of the demo): the host copies the link.</summary>
    NoGuestEmail,

    /// <summary>The booking may not have a stay link (a request, a cancelled booking, a no-show, a stay over by 30 days).</summary>
    NotEligible,

    /// <summary>The queue of the e-mails did not take it (no provider configured, a queue that refused it).</summary>
    NotQueued,
}
