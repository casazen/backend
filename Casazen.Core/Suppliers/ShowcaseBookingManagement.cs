using System.Diagnostics.CodeAnalysis;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Core.Suppliers;

/// <summary>
/// What the customer of a supplier's public showcase types to find its booking again (SP-11): the supplier's slug, the code of the
/// booking (<c>XXXXX-XXXXX</c>, in the e-mails) and the e-mail address the booking was made with. Nothing is checked yet:
/// <see cref="ShowcaseBookingManagementRules.TryNormalize"/> does.
/// </summary>
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
    ShowcaseBookingActions Actions);

/// <param name="Name">The name of the service when the request was made.</param>
/// <param name="Slug">
/// The public slug of the service, while it is published (the page asks <c>GET api/public/suppliers/{slug}/slots?service=</c>
/// for the free times to move the booking to); <c>null</c> once the supplier paused or deleted it.
/// </param>
public sealed record ShowcaseBookingServiceInfo(string Name, string? Slug);

/// <summary>The supplier: the name its showcase shows and the slug of that showcase.</summary>
public sealed record ShowcaseBookingSupplierInfo(string Name, string Slug);

/// <summary>
/// Where the work is: the comune and the postal code always, the street address, the floor and the notes for the access only once
/// the supplier took the request (<c>null</c> before, and once the retention removed them).
/// </summary>
public sealed record ShowcaseBookingPlace(string City, string? PostalCode, string? Address, string? Floor, string? AccessNotes);

/// <summary>
/// One line of the price, in cents of euro: the choices of the customer at the time of the booking, or — once the supplier
/// completed the work — the lines of the final amount.
/// </summary>
/// <param name="Kind"><c>service</c> (the work), <c>option</c> (a supplement the customer picked) or <c>extra</c> (added when the work was completed).</param>
public sealed record ShowcaseBookingPriceLine(string Kind, string Label, int Quantity, int UnitAmountCents, int AmountCents);

/// <summary>
/// The price of the booking in cents of euro: what was estimated when it was made, what the supplier quoted when it took the
/// request, the final total, and the lines. <see cref="AmountCents"/> is the one to show.
/// </summary>
public sealed record ShowcaseBookingPrice(
    int? EstimatedAmountCents,
    int? QuotedAmountCents,
    int? FinalAmountCents,
    IReadOnlyList<ShowcaseBookingPriceLine> Lines)
{
    /// <summary>The final total, else the supplier's quote, else the estimate; <c>null</c> is "to agree with the supplier".</summary>
    public int? AmountCents => FinalAmountCents ?? QuotedAmountCents ?? EstimatedAmountCents;

    /// <summary><c>final</c>, <c>quote</c> or <c>estimate</c>: which of the three <see cref="AmountCents"/> is; <c>null</c> when there is none.</summary>
    public string? Basis => FinalAmountCents is not null ? "final" : QuotedAmountCents is not null ? "quote" : EstimatedAmountCents is not null ? "estimate" : null;
}

/// <summary>
/// Another time the supplier proposed instead of the requested one (it is an offer: the request keeps the requested time until the
/// customer accepts).
/// </summary>
/// <param name="AnswerBy">The instant the customer has to answer by; after it the request is cancelled.</param>
/// <param name="Message">What the supplier wrote with the proposal (the customer was e-mailed it).</param>
public sealed record ShowcaseBookingProposal(DateTime StartUtc, DateTime EndUtc, DateTime ProposedAt, DateTime? AnswerBy, string? Message);

/// <summary>A cancelled booking: when, by whom (the customer, the supplier, or CasaZen for no answer) and the reason a person wrote.</summary>
/// <param name="Reason">The text a person wrote; <c>null</c> when the cancellation has no text (a code is not a sentence).</param>
public sealed record ShowcaseBookingCancellation(DateTime At, ServiceRequestActorParty By, string? Reason);

/// <summary>
/// The terms of cancelling (decision D6: no exit cost in v1, so a late cancellation costs nothing either): until when it is free,
/// as <see cref="ShowcaseBookingManagementRules.FreeCancellationUntil"/>, and whether it still is.
/// </summary>
public sealed record ShowcaseBookingCancellationTerms(DateTime FreeUntilUtc, bool IsFree);

/// <summary>What the customer may do now with the booking (<see cref="ShowcaseBookingManagementRules"/>).</summary>
public sealed record ShowcaseBookingActions(bool CanCancel, bool CanReschedule, bool CanRespondToProposal);

/// <summary>
/// Codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of what the customer's
/// own area of a booking from a supplier's showcase refuses (SP-11). Snake case and never renamed: the frontend branches on them.
/// The bookings' other refusals are those of <see cref="ShowcaseBookingErrors"/> (<c>supplier_slot_unavailable</c> 409 for a time
/// that is not free, <c>supplier_booking_supplier_unavailable</c> for a supplier that is not active, <c>supplier_booking_invalid</c>
/// with its <c>fields</c>) and <c>service_request_state_changed</c> 409 when the supplier acted at the same moment.
/// </summary>
public static class ShowcaseBookingManagementErrors
{
    /// <summary>
    /// 404: no booking matches. <b>One answer</b>, with the same body, for a supplier that does not exist, a code that does not
    /// exist or has a wrong format, a code of another supplier, and an e-mail address that is not the one of the booking: nothing
    /// tells which of them it was.
    /// </summary>
    public const string NotFound = "supplier_booking_not_found";

    /// <summary>422: the booking is not in a status the customer can cancel from (in progress, done, refused or already cancelled), or its time has passed.</summary>
    public const string CannotCancel = "supplier_booking_cannot_cancel";

    /// <summary>422: the time can be changed only while the request is new, the supplier is active and the service is still published.</summary>
    public const string CannotReschedule = "supplier_booking_cannot_reschedule";

    /// <summary>422: the booking has no proposed time to answer (never made, already answered, or the request moved on).</summary>
    public const string NoProposal = "supplier_booking_no_proposal";

    /// <summary>422: the time to answer the proposed time has passed; the request is cancelled by the next run of the upkeep job.</summary>
    public const string ProposalExpired = "supplier_booking_proposal_expired";

    public const string NotFoundMessageKey = "SupplierBookingNotFound";
    public const string CannotCancelMessageKey = "SupplierBookingCannotCancel";
    public const string CannotRescheduleMessageKey = "SupplierBookingCannotReschedule";
    public const string NoProposalMessageKey = "SupplierBookingNoProposal";
    public const string ProposalExpiredMessageKey = "SupplierBookingProposalExpired";

    /// <summary>Every <c>SharedResources</c> key the area raises (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        NotFoundMessageKey,
        CannotCancelMessageKey,
        CannotRescheduleMessageKey,
        NoProposalMessageKey,
        ProposalExpiredMessageKey,
    ];

    /// <summary>404: no booking matches the slug, the code and the e-mail address (see <see cref="NotFound"/>).</summary>
    public static NotFoundException BookingNotFound() =>
        new("No showcase booking matches the supplier, the code and the e-mail address")
        {
            Code = NotFound,
            MessageKey = NotFoundMessageKey,
        };

    /// <summary>422: the booking cannot be cancelled by the customer now.</summary>
    public static DomainRuleException CancelRefused() => new(CannotCancel, CannotCancelMessageKey);

    /// <summary>422: the time of the booking cannot be changed now.</summary>
    public static DomainRuleException RescheduleRefused() => new(CannotReschedule, CannotRescheduleMessageKey);

    /// <summary>422: there is no proposed time to answer.</summary>
    public static DomainRuleException ProposalMissing() => new(NoProposal, NoProposalMessageKey);

    /// <summary>422: the time to answer the proposed time has passed.</summary>
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

    /// <summary>
    /// The last instant a cancellation is free: <paramref name="freeCancellationHours"/> elapsed hours before
    /// <paramref name="startUtc"/>. With 0 hours it is the start itself.
    /// </summary>
    public static DateTime FreeCancellationUntil(DateTime startUtc, int freeCancellationHours) =>
        startUtc.AddHours(-Math.Max(0, freeCancellationHours));

    /// <summary>True while a cancellation at <paramref name="nowUtc"/> is still free (at the deadline itself it is).</summary>
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

    /// <summary>
    /// True when the customer may move the time: only a new request that has a time, of a supplier that is active, for a service that
    /// is still published (the page needs its free slots, and the planner needs its duration and rules).
    /// </summary>
    public static bool CanReschedule(ServiceRequestStatus status, bool hasTime, bool supplierActive, bool serviceBookable) =>
        status == ServiceRequestStatus.Richiesto && hasTime && supplierActive && serviceBookable;

    /// <summary>
    /// True when the customer may accept or turn down the proposed time: the request is new, a proposal is waiting, the deadline to
    /// answer it (<paramref name="answerByUtc"/>) has not passed and the supplier is active (accepting takes the request on its behalf).
    /// </summary>
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

    /// <summary>
    /// The reason a customer gave when it cancelled: trimmed, with the lines it wrote, or <c>null</c> when there is none. A reason
    /// that is longer than <see cref="ServiceRequestLimits.CancellationReasonMaxLength"/> or has control characters is a 422 naming
    /// the <c>reason</c> field.
    /// </summary>
    /// <exception cref="ShowcaseBookingRuleException">422 <see cref="ShowcaseBookingErrors.Invalid"/> for <see cref="ShowcaseBookingFields.Reason"/>.</exception>
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
