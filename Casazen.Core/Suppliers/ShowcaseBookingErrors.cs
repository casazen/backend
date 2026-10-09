using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the booking
/// from a supplier's showcase (SP-10). The codes are snake_case and never renamed: the frontend branches on them. A slot that
/// is not free is the <c>supplier_slot_unavailable</c> of SP-04 (<c>ServiceRequestErrorCodes.SlotUnavailable</c>, 409), and an
/// unknown or inactive supplier, or a service that is not published, answers the 404 of SP-09.
/// </summary>
public static class ShowcaseBookingErrors
{
    /// <summary>422: a value of the booking is not valid (<see cref="ShowcaseBookingRuleException.Fields"/> names them).</summary>
    public const string Invalid = "supplier_booking_invalid";

    /// <summary>422: the supplier does not take bookings online right now (<c>SupplierSettings.OnlineBookingEnabled</c> is off).</summary>
    public const string Offline = "supplier_booking_offline";

    /// <summary>422: the comune is not one of the supplier's zones: the customer asks for a quote instead.</summary>
    public const string OutsideZone = "supplier_booking_outside_zone";

    /// <summary>422: the customer did not accept the privacy notice.</summary>
    public const string ConsentRequired = "supplier_booking_consent_required";

    /// <summary>422: the customer accepted a version of the privacy notice that is not the current one.</summary>
    public const string ConsentOutdated = "supplier_booking_consent_outdated";

    /// <summary>
    /// 404: the link of the e-mail check is not valid. One answer for a booking that does not exist, one of another supplier
    /// and a token that does not match: the link tells nothing about other bookings.
    /// </summary>
    public const string LinkInvalid = "supplier_booking_link_invalid";

    /// <summary>409: the link is right but the time to check the e-mail (30 minutes by default) has passed: the booking starts again.</summary>
    public const string LinkExpired = "supplier_booking_link_expired";

    /// <summary>
    /// 422: the supplier is not active anymore (suspended, or not activated) when the customer checks the e-mail: the request
    /// is not created.
    /// </summary>
    public const string SupplierUnavailable = "supplier_booking_supplier_unavailable";

    /// <summary>The <c>SharedResources</c> key of the 429 that also stops a fourth unverified booking of the same e-mail address.</summary>
    public const string RateLimitedMessageKey = "RateLimitedDetail";

    /// <summary>Every <c>SharedResources</c> key the booking raises (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        "SupplierBookingInvalid",
        "SupplierBookingOffline",
        "SupplierBookingOutsideZone",
        "SupplierBookingConsentRequired",
        "SupplierBookingConsentOutdated",
        "SupplierBookingLinkInvalid",
        "SupplierBookingLinkExpired",
        "SupplierBookingSupplierUnavailable",
    ];

    /// <summary>The 422 of a booking with values that are not valid; <paramref name="fields"/> are the JSON names of what is wrong.</summary>
    public static ShowcaseBookingRuleException InvalidFields(IReadOnlyList<string> fields) =>
        new(Invalid, "SupplierBookingInvalid", fields);

    /// <summary>422: the supplier takes no online bookings now.</summary>
    public static DomainRuleException OfflineSupplier() => new(Offline, "SupplierBookingOffline");

    /// <summary>422: the comune is outside the supplier's zones.</summary>
    public static DomainRuleException OutsideSupplierZone() => new(OutsideZone, "SupplierBookingOutsideZone");

    /// <summary>422: no consent.</summary>
    public static DomainRuleException NoConsent() => new(ConsentRequired, "SupplierBookingConsentRequired");

    /// <summary>422: not the current version of the notice.</summary>
    public static DomainRuleException OldConsent() => new(ConsentOutdated, "SupplierBookingConsentOutdated");

    /// <summary>404: the link of the e-mail check is not valid.</summary>
    public static NotFoundException InvalidLink() =>
        new("Showcase booking link not valid") { Code = LinkInvalid, MessageKey = "SupplierBookingLinkInvalid" };

    /// <summary>409: the time to check the e-mail has passed.</summary>
    public static DomainConflictException ExpiredLink() => new(LinkExpired, "SupplierBookingLinkExpired");

    /// <summary>422: the supplier is not active.</summary>
    public static DomainRuleException InactiveSupplier() => new(SupplierUnavailable, "SupplierBookingSupplierUnavailable");
}

/// <summary>
/// Names of the fields of a booking request, as the client sends them (what a 422 <c>supplier_booking_invalid</c> lists in
/// <c>fields</c>). The fields of the estimate (<c>service</c>, <c>quantity</c>, <c>surfaceSqm</c>, <c>options[0].code</c>...) are the
/// ones of <see cref="SupplierQuoteFields"/>.
/// </summary>
public static class ShowcaseBookingFields
{
    public const string ClientRequestId = "clientRequestId";
    public const string StartUtc = "startUtc";
    public const string ComuneIstat = "comuneIstat";
    public const string City = "city";
    public const string PostalCode = "postalCode";
    public const string Address = "address";
    public const string Floor = "floor";
    public const string AccessNotes = "accessNotes";
    public const string FullName = "fullName";
    public const string Email = "email";
    public const string Phone = "phone";
    public const string PrivacyNoticeVersion = "privacyNoticeVersion";
    public const string Token = "token";
}

/// <summary>
/// A 422 of the booking that names the fields at fault: the controller adds them to the response as <c>fields</c>, so the form
/// can mark them. Like <see cref="SupplierQuoteRuleException"/>, the message arguments are the field names.
/// </summary>
public sealed class ShowcaseBookingRuleException : DomainRuleException
{
    public ShowcaseBookingRuleException(string code, string messageKey, IReadOnlyList<string> fields)
        : base(code, messageKey, string.Join(", ", fields))
    {
        Fields = fields;
    }

    /// <summary>JSON names of the fields at fault.</summary>
    public IReadOnlyList<string> Fields { get; }
}

/// <summary>
/// An e-mail address already has <see cref="ShowcaseBookingLimits.MaxUnverifiedHoldsPerEmail"/> bookings of this supplier waiting
/// for the e-mail check. The API answers it as the per-IP and per-address limits do (429 <c>rate_limited</c> with
/// <c>Retry-After</c>): the three look alike, and none says whether the address is known.
/// </summary>
public sealed class ShowcaseBookingTooManyHoldsException(TimeSpan retryAfter)
    : Exception("Too many showcase bookings waiting for the e-mail check")
{
    /// <summary>When the oldest of them lapses, at the latest: the earliest a new booking can be made.</summary>
    public TimeSpan RetryAfter { get; } = retryAfter;
}
