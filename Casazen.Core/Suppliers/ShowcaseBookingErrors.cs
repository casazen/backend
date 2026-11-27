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
    public const string Invalid = "supplier_booking_invalid";

    public const string Offline = "supplier_booking_offline";

    public const string OutsideZone = "supplier_booking_outside_zone";

    public const string ConsentRequired = "supplier_booking_consent_required";

    public const string ConsentOutdated = "supplier_booking_consent_outdated";

    public const string LinkInvalid = "supplier_booking_link_invalid";

    public const string LinkExpired = "supplier_booking_link_expired";

    public const string SupplierUnavailable = "supplier_booking_supplier_unavailable";

    public const string RateLimitedMessageKey = "RateLimitedDetail";

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

    public static ShowcaseBookingRuleException InvalidFields(IReadOnlyList<string> fields) =>
        new(Invalid, "SupplierBookingInvalid", fields);

    public static DomainRuleException OfflineSupplier() => new(Offline, "SupplierBookingOffline");

    public static DomainRuleException OutsideSupplierZone() => new(OutsideZone, "SupplierBookingOutsideZone");

    public static DomainRuleException NoConsent() => new(ConsentRequired, "SupplierBookingConsentRequired");

    public static DomainRuleException OldConsent() => new(ConsentOutdated, "SupplierBookingConsentOutdated");

    public static NotFoundException InvalidLink() =>
        new("Showcase booking link not valid") { Code = LinkInvalid, MessageKey = "SupplierBookingLinkInvalid" };

    public static DomainConflictException ExpiredLink() => new(LinkExpired, "SupplierBookingLinkExpired");

    public static DomainRuleException InactiveSupplier() => new(SupplierUnavailable, "SupplierBookingSupplierUnavailable");
}

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

    public const string Message = "message";

    public const string Reason = "reason";
}

public sealed class ShowcaseBookingRuleException : DomainRuleException
{
    public ShowcaseBookingRuleException(string code, string messageKey, IReadOnlyList<string> fields)
        : base(code, messageKey, string.Join(", ", fields))
    {
        Fields = fields;
    }

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
    public TimeSpan RetryAfter { get; } = retryAfter;
}
