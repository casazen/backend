using System.Diagnostics.CodeAnalysis;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Core.Suppliers;

public sealed record ShowcaseBookingCredentials(string? Slug, string? Code, string? Email);

/// <summary>
/// The credentials in the form they are looked up in: the lowercase slug, the code without separator
/// (<see cref="BookingCodes"/>) and the lowercase e-mail address. Only the first two ever reach the database, as predicates; the
/// address is compared after the stored one has been decrypted (<see cref="ServiceCustomerEmails.SameAddress"/>).
/// </summary>
public sealed record ShowcaseBookingAccessKey(string Slug, string Code, string Email);

/// <summary>
/// The booking as its customer sees it (SP-11): what <c>POST api/public/supplier-bookings/lookup</c> and every action of the
/// customer answer. <b>Built only from the fields listed here</b>: the notes the supplier left for itself (how the work went), the
/// photos, the member who took the request, the customer's own name, e-mail address and phone, and anything of another customer are
/// not in it, whatever the request has. The exact address, the floor and the notes for the access come back only once the
/// supplier took the request (<see cref="SupplierJobDisclosure.IsDisclosed"/>), as for the supplier.
/// </summary>
/// <param name="PublicCode">The code of the booking, stored form (<see cref="BookingCodes"/>): the page shows it as <c>XXXXX-XXXXX</c>.</param>
/// <param name="RespondBy">When the supplier has to answer by, while the request is new and has no proposal; <c>null</c> otherwise.</param>
/// <param name="RejectionReason">The reason the supplier gave when it refused the request (the customer was e-mailed it).</param>
/// <param name="Kind">
/// <see cref="ServiceRequestKind.Custom"/> for a request on measure (SP-10b), which has no time and no price (<c>StartUtc</c> and <c>EndUtc</c> are its
/// creation, to be ignored) and a conversation; <see cref="ServiceRequestKind.Standard"/> for every other booking, whose view is what it always was.
/// </param>
/// <param name="Thread">The conversation of a request on measure, with the supplier's messages marked as read; <c>null</c> for any other booking.</param>
/// <param name="Negotiation">Where the negotiation is (<see cref="ServiceRequestNegotiation"/>); <c>null</c> for any other booking.</param>
/// <param name="Quote">The quote the supplier sent and the customer has not answered (SP-04b); <c>null</c> when there is none. Never set for a request on measure, whose quote is its <paramref name="Offer"/>.</param>
/// <param name="Payment">The money of a booking paid inside CasaZen (SP-15c-a); <c>null</c> when the booking is not.</param>
/// <param name="Offer">The offer the supplier sent for a request on measure and the customer has not answered (SP-10b-b); <c>null</c> when there is none, and for any other booking.</param>
/// <param name="OfferPaymentRequired">SP-10b-b, with an <paramref name="Offer"/>: accepting it is paid inside CasaZen (the card is blocked, then charged when the request is taken); <c>null</c> without an offer.</param>
/// <param name="PaymentSession">SP-10b-b: what the browser needs to block the card, only in the answer of the acceptance that asks for a card; <c>null</c> in every other answer.</param>
/// <param name="Alternatives">
/// SP-10c: the other suppliers offered to the customer of a request that <b>CasaZen closed because the supplier did not answer in time</b>
/// (<see cref="ShowcaseAlternativeRules.Offers"/>), at most three, best first; an empty list when there is none to offer; <c>null</c> for every other
/// booking, whose answer is what it always was.
/// </param>
public sealed record ShowcaseBookingView(
    string PublicCode,
    ServiceRequestStatus Status,
    ShowcaseBookingServiceInfo Service,
    ShowcaseBookingSupplierInfo Supplier,
    DateTime StartUtc,
    DateTime EndUtc,
    ShowcaseBookingPlace Place,
    ShowcaseBookingPrice Price,
    DateTime? RespondBy,
    ShowcaseBookingProposal? Proposal,
    ShowcaseBookingCancellation? Cancellation,
    string? RejectionReason,
    ShowcaseBookingCancellationTerms CancellationTerms,
    ShowcaseBookingActions Actions,
    ServiceRequestKind Kind = ServiceRequestKind.Standard,
    ServiceRequestThread? Thread = null,
    string? Negotiation = null,
    ShowcaseBookingQuote? Quote = null,
    ShowcaseBookingPaymentView? Payment = null,
    ServiceRequestOffer? Offer = null,
    bool? OfferPaymentRequired = null,
    OfferPaymentSession? PaymentSession = null,
    IReadOnlyList<ShowcaseAlternative>? Alternatives = null);

/// <summary>
/// The money of a booking paid inside CasaZen as its customer reads it (SP-15c-a): what the card is blocked or charged for, where the money
/// is, and, while CasaZen holds it, the moment it goes to the supplier unless the customer says "all fine" first. Amounts only: nothing of
/// Stripe, no id, no card.
/// </summary>
/// <param name="Status">
/// <see cref="ShowcaseBookingManagementRules.PaymentStatusOf"/>: <c>authorized</c> (the card is blocked, nothing is charged), <c>held</c>
/// (charged, CasaZen holds it), <c>released</c> (the supplier was paid), <c>refunded</c>, <c>canceled</c> (the block was released: nothing
/// was charged) or <c>review</c> (CasaZen is looking at it).
/// </param>
/// <param name="AmountCents">The total that was blocked and, once the supplier accepted, charged.</param>
/// <param name="Currency">Uppercase ISO currency.</param>
/// <param name="RefundedCents">What was given back to the customer.</param>
/// <param name="ReleaseAtUtc">While the money is held and the work is done: when it goes to the supplier if the customer says nothing; otherwise <c>null</c>.</param>
/// <param name="Dispute">SP-15c-c: the customer's complaint about the job, while it is open and after it was decided; <c>null</c> when there is none.</param>
public sealed record ShowcaseBookingPaymentView(
    string Status,
    int AmountCents,
    string Currency,
    int RefundedCents,
    DateTime? ReleaseAtUtc,
    ShowcaseBookingDisputeView? Dispute = null);

/// <summary>
/// The customer's complaint about the job, as the customer reads it (SP-15c-c): its own words, the state and the decision. The supplier's reply and
/// the note of the admin who decided are not in it; the customer is told that the supplier replied, not what it wrote.
/// </summary>
/// <param name="Status"><c>AwaitingSupplier</c>, <c>AwaitingDecision</c>, <c>ResolvedForSupplier</c>, <c>ResolvedForCustomer</c> or <c>Withdrawn</c>.</param>
/// <param name="Reason"><c>not_done</c>, <c>incomplete</c>, <c>poor_quality</c>, <c>not_as_described</c> or <c>other</c>.</param>
/// <param name="NoShow">The supplier never marked the work completed and the time agreed for it ended.</param>
/// <param name="OpenedByCasaZen">CasaZen itself opened it (the no-show), not the customer.</param>
/// <param name="Text">What the customer wrote.</param>
/// <param name="OpenedAt">When it was opened.</param>
/// <param name="SupplierReplyDueAt">When the supplier's time to reply ends.</param>
/// <param name="SupplierReplied">The supplier replied.</param>
/// <param name="ResolvedAt">When it was decided or taken back.</param>
/// <param name="RefundCents">What goes back to the customer, when it was decided for the customer.</param>
/// <param name="Settled">The refund that was decided has been made.</param>
public sealed record ShowcaseBookingDisputeView(
    string Status,
    string Reason,
    bool NoShow,
    bool OpenedByCasaZen,
    string? Text,
    DateTime OpenedAt,
    DateTime SupplierReplyDueAt,
    bool SupplierReplied,
    DateTime? ResolvedAt,
    int? RefundCents,
    bool Settled);

public sealed record ShowcaseBookingQuote(
    DateTime SentAt,
    IReadOnlyList<ServiceRequestQuoteLine> Lines,
    int TotalCents,
    string? Note,
    DateTime? StartUtc,
    DateTime? EndUtc,
    DateTime? AnswerBy);

public sealed record ShowcaseBookingServiceInfo(string Name, string? Slug);

public sealed record ShowcaseBookingSupplierInfo(string Name, string Slug);

public sealed record ShowcaseBookingPlace(string City, string? PostalCode, string? Address, string? Floor, string? AccessNotes);

public sealed record ShowcaseBookingPriceLine(string Kind, string Label, int Quantity, int UnitAmountCents, int AmountCents);

public sealed record ShowcaseBookingPrice(
    int? EstimatedAmountCents,
    int? QuotedAmountCents,
    int? FinalAmountCents,
    IReadOnlyList<ShowcaseBookingPriceLine> Lines)
{
    public int? AmountCents => FinalAmountCents ?? QuotedAmountCents ?? EstimatedAmountCents;

    public string? Basis => FinalAmountCents is not null ? "final" : QuotedAmountCents is not null ? "quote" : EstimatedAmountCents is not null ? "estimate" : null;
}

/// <summary>
/// Another time the supplier proposed instead of the requested one (it is an offer: the request keeps the requested time until the
/// customer accepts).
/// </summary>
/// <param name="AnswerBy">The instant the customer has to answer by; after it the request is cancelled.</param>
/// <param name="Message">What the supplier wrote with the proposal (the customer was e-mailed it).</param>
public sealed record ShowcaseBookingProposal(DateTime StartUtc, DateTime EndUtc, DateTime ProposedAt, DateTime? AnswerBy, string? Message);

public sealed record ShowcaseBookingCancellation(DateTime At, ServiceRequestActorParty By, string? Reason);

public sealed record ShowcaseBookingCancellationTerms(DateTime FreeUntilUtc, bool IsFree);

/// <summary>What the customer may do now with the booking (<see cref="ShowcaseBookingManagementRules"/>).</summary>
/// <param name="CanConfirmOk">SP-15c-a: the work is done and CasaZen holds the money: the customer may say "all fine" and the supplier is paid at once.</param>
/// <param name="CanReportProblem">SP-15c-c: the customer may report a problem — the work is done and the time to speak up has not ended, or the supplier never marked it completed and the time agreed for it has ended.</param>
/// <param name="CanWithdrawProblem">SP-15c-c: a complaint of the customer is open and the customer may take it back.</param>
/// <param name="CanRespondToOffer">SP-10b-b: an offer waits on a request on measure, its time to answer has not passed and the supplier is active: the customer may accept it or turn it down.</param>
public sealed record ShowcaseBookingActions(
    bool CanCancel,
    bool CanReschedule,
    bool CanRespondToProposal,
    bool CanRespondToQuote = false,
    bool CanConfirmOk = false,
    bool CanReportProblem = false,
    bool CanWithdrawProblem = false,
    bool CanRespondToOffer = false);

/// <summary>
/// Codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of what the customer's
/// own area of a booking from a supplier's showcase refuses (SP-11). Snake case and never renamed: the frontend branches on them.
/// The bookings' other refusals are those of <see cref="ShowcaseBookingErrors"/> (<c>supplier_slot_unavailable</c> 409 for a time
/// that is not free, <c>supplier_booking_supplier_unavailable</c> for a supplier that is not active, <c>supplier_booking_invalid</c>
/// with its <c>fields</c>) and <c>service_request_state_changed</c> 409 when the supplier acted at the same moment.
/// </summary>
public static class ShowcaseBookingManagementErrors
{
    public const string NotFound = "supplier_booking_not_found";

    public const string CannotCancel = "supplier_booking_cannot_cancel";

    public const string CannotReschedule = "supplier_booking_cannot_reschedule";

    public const string NoProposal = "supplier_booking_no_proposal";

    public const string ProposalExpired = "supplier_booking_proposal_expired";

    public const string NotFoundMessageKey = "SupplierBookingNotFound";
    public const string CannotCancelMessageKey = "SupplierBookingCannotCancel";
    public const string CannotRescheduleMessageKey = "SupplierBookingCannotReschedule";
    public const string NoProposalMessageKey = "SupplierBookingNoProposal";
    public const string ProposalExpiredMessageKey = "SupplierBookingProposalExpired";

    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        NotFoundMessageKey,
        CannotCancelMessageKey,
        CannotRescheduleMessageKey,
        NoProposalMessageKey,
        ProposalExpiredMessageKey,
    ];

    public static NotFoundException BookingNotFound() =>
        new("No showcase booking matches the supplier, the code and the e-mail address")
        {
            Code = NotFound,
            MessageKey = NotFoundMessageKey,
        };

    public static DomainRuleException CancelRefused() => new(CannotCancel, CannotCancelMessageKey);

    public static DomainRuleException RescheduleRefused() => new(CannotReschedule, CannotRescheduleMessageKey);

    public static DomainRuleException ProposalMissing() => new(NoProposal, NoProposalMessageKey);

    public static DomainRuleException ProposalLapsed() => new(ProposalExpired, ProposalExpiredMessageKey);
}

/// <summary>
/// The rules of the customer's own area of a booking (SP-11), as pure functions: no clock, no database. The lookup shows what they
/// allow (<see cref="ShowcaseBookingActions"/>) and the actions enforce the same ones, so the page and the API cannot disagree.
/// </summary>
/// <remarks>
/// <para><b>States and actions.</b> The customer can cancel a new request (<c>Richiesto</c>) at any time and a taken one
/// (<c>PresoInCarico</c>) until its time comes; never one in progress, done, paid, refused or cancelled. It can move the time only
/// of a new request, and only while the supplier is active and the service is still published; it can answer a proposed time
/// only while the request is new, the proposal is waiting, its deadline has not passed and the supplier is active.</para>
/// <para><b>The 24 hours</b> (<see cref="FreeCancellationUntil"/>) are elapsed hours, counted back from the start of the work on the
/// UTC line: on the Sunday the clocks go back (25 October 2026) 24 hours before 08:00 is 09:00 of the Saturday, on the clock of that
/// day. The page shows the instant with the offset of Rome (<see cref="Utilities.RomeCalendar.ToRome"/>).</para>
/// </remarks>
public static class ShowcaseBookingManagementRules
{
    /// <summary>
    /// The credentials in the form they are looked up in, or <c>false</c> for something that cannot be a booking: a slug that is not
    /// one, a code that is not a <see cref="BookingCodes"/> code, an e-mail address that is empty, too long or has control
    /// characters. The caller answers it exactly as a booking that does not exist.
    /// </summary>
    public static bool TryNormalize(ShowcaseBookingCredentials? credentials, [NotNullWhen(true)] out ShowcaseBookingAccessKey? key)
    {
        key = null;
        if (credentials is null)
            return false;

        var slug = SupplierShowcaseSlug.Normalize(credentials.Slug);
        if (!SupplierShowcaseSlug.IsLookupable(slug, PublicShowcaseLimits.SlugMaxLength))
            return false;

        if (!BookingCodes.TryNormalize(credentials.Code, out var code))
            return false;

        var email = ShowcaseBookingRules.NormalizeEmail(credentials.Email ?? string.Empty);
        if (email.Length == 0 || email.Length > ShowcaseBookingLimits.EmailMaxLength || email.Any(char.IsControl))
            return false;

        key = new ShowcaseBookingAccessKey(slug, code, email);
        return true;
    }

    public static DateTime FreeCancellationUntil(DateTime startUtc, int freeCancellationHours) =>
        startUtc.AddHours(-Math.Max(0, freeCancellationHours));

    public static bool IsFreeCancellation(DateTime startUtc, DateTime nowUtc, int freeCancellationHours) =>
        nowUtc <= FreeCancellationUntil(startUtc, freeCancellationHours);

    /// <summary>
    /// True when the customer may cancel a booking in <paramref name="status"/> at <paramref name="nowUtc"/>: a new request any time
    /// (nothing was agreed yet), a taken one until its start (<paramref name="startUtc"/>; once the time has passed the work may
    /// have been done, and a customer cancelling after it would take away what the supplier is owed); never the others.
    /// </summary>
    public static bool CanCancel(ServiceRequestStatus status, DateTime? startUtc, DateTime nowUtc) => status switch
    {
        ServiceRequestStatus.Richiesto => true,
        ServiceRequestStatus.PresoInCarico => startUtc is not { } start || nowUtc < start,
        _ => false,
    };

    public static bool CanReschedule(
        ServiceRequestStatus status,
        bool hasTime,
        bool supplierActive,
        bool serviceBookable,
        bool quotePending = false) =>
        status == ServiceRequestStatus.Richiesto && hasTime && supplierActive && serviceBookable && !quotePending;

    public static bool CanRespondToQuote(
        ServiceRequestStatus status,
        bool quotePending,
        DateTime? answerByUtc,
        DateTime nowUtc,
        bool supplierActive) =>
        status == ServiceRequestStatus.Richiesto
        && quotePending
        && supplierActive
        && answerByUtc is { } answerBy
        && nowUtc < answerBy;

    public static bool CanRespondToOffer(
        ServiceRequestStatus status,
        bool offerPending,
        DateTime? validUntilUtc,
        DateTime nowUtc,
        bool supplierActive) =>
        CanRespondToQuote(status, offerPending, validUntilUtc, nowUtc, supplierActive);

    public static bool CanRespondToProposal(
        ServiceRequestStatus status,
        bool proposalPending,
        DateTime? answerByUtc,
        DateTime nowUtc,
        bool supplierActive) =>
        status == ServiceRequestStatus.Richiesto
        && proposalPending
        && supplierActive
        && answerByUtc is { } answerBy
        && nowUtc < answerBy;

    public static string PaymentStatusOf(ServicePaymentStatus status) => status switch
    {
        ServicePaymentStatus.Requested or ServicePaymentStatus.Failed => "awaiting_card",
        ServicePaymentStatus.Authorized => "authorized",
        ServicePaymentStatus.Held or ServicePaymentStatus.TransferFailed or ServicePaymentStatus.Disputed => "held",
        ServicePaymentStatus.Released => "released",
        ServicePaymentStatus.Refunded or ServicePaymentStatus.PartiallyRefunded => "refunded",
        ServicePaymentStatus.Canceled => "canceled",
        _ => "review",
    };

    public static bool CanConfirmOk(ServiceRequestStatus requestStatus, ServicePaymentStatus paymentStatus, int netCents, int refundedCents) =>
        requestStatus == ServiceRequestStatus.Completato
        && paymentStatus is ServicePaymentStatus.Held or ServicePaymentStatus.TransferFailed
        && HeldPaymentRules.ReleasableCents(netCents, refundedCents, 0) > 0;

    public static string? NormalizeReason(string? reason)
    {
        var text = reason?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.Length > ServiceRequestLimits.CancellationReasonMaxLength
            || text.Any(c => char.IsControl(c) && c != '\n' && c != '\t'))
        {
            throw ShowcaseBookingErrors.InvalidFields([ShowcaseBookingFields.Reason]);
        }

        return text;
    }
}
