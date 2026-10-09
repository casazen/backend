namespace Casazen.Core.Suppliers;

/// <summary>
/// Technical bounds of the booking an anonymous customer makes from a supplier's showcase (SP-10): the size of every field, the
/// number of unverified bookings per e-mail address, the size of the body. They live in one place so the rules, the
/// database columns, the request DTO and the docs cannot drift apart; like the other limits of the suppliers they keep an
/// anonymous endpoint small and a typo harmless, they are not product rules.
/// </summary>
public static class ShowcaseBookingLimits
{
    /// <summary>Longest full name of the customer.</summary>
    public const int FullNameMaxLength = 100;

    /// <summary>Longest e-mail address (RFC 5321).</summary>
    public const int EmailMaxLength = 254;

    /// <summary>Fewest and most digits of a phone number (E.164 has at most 15).</summary>
    public const int PhoneMinDigits = 6;

    /// <inheritdoc cref="PhoneMinDigits"/>
    public const int PhoneMaxDigits = 15;

    /// <summary>Longest comune name written by the customer (the column of <c>Property.City</c> is 100).</summary>
    public const int CityMaxLength = 100;

    /// <summary>A postal code (CAP) has exactly five digits.</summary>
    public const int PostalCodeLength = 5;

    /// <summary>Longest street address.</summary>
    public const int AddressMaxLength = 200;

    /// <summary>Longest "floor and apartment" text.</summary>
    public const int FloorMaxLength = 60;

    /// <summary>Longest note for the access (doorbell, keys): the one free-text field of the booking.</summary>
    public const int AccessNotesMaxLength = 500;

    /// <summary>Longest version of the privacy notice the customer accepted (<c>Suppliers:Showcase:PrivacyNoticeVersion</c>).</summary>
    public const int PrivacyNoticeVersionMaxLength = 50;

    /// <summary>Longest client address kept as consent evidence (an IPv6 address in text form).</summary>
    public const int ConsentIpMaxLength = 45;

    /// <summary>The languages of the customer's e-mails (<see cref="ServiceCustomerLocales"/>); anything else is Italian.</summary>
    public const int LocaleLength = 2;

    /// <summary>
    /// Bookings of one e-mail address, for one supplier, that may wait for the e-mail check at the same time (decision of
    /// <c>gap/05</c> §4.2): the fourth is refused until one is checked or lapses. It keeps one address from holding many slots.
    /// </summary>
    public const int MaxUnverifiedHoldsPerEmail = 3;

    /// <summary>
    /// Days behind today a start can still be a slot of the planner (the notice is never negative; the day covers the difference
    /// between UTC and Rome). Anything earlier is a slot that is not free, answered before any date arithmetic runs on it.
    /// </summary>
    public const int SlotWindowBehindDays = 1;

    /// <summary>
    /// Days ahead of today a start can still be a slot of the planner: the largest horizon a supplier can set
    /// (<see cref="SupplierAgendaLimits.MaxHorizonDays"/>) and a day on each side for the time zones. Anything later is a slot that is
    /// not free; the date at the end of the calendar that anyone can send would overflow the arithmetic of the planner.
    /// </summary>
    public const int SlotWindowAheadDays = SupplierAgendaLimits.MaxHorizonDays + 2;

    /// <summary>Largest body of a booking request, in bytes: a handful of short fields and one note, never a document.</summary>
    public const int CreateMaxBodyBytes = 16 * 1024;

    /// <summary>Largest body of the e-mail check: the token and nothing else.</summary>
    public const int ConfirmMaxBodyBytes = 1024;

    /// <summary>
    /// Largest body of a call of the customer's own area of a booking (SP-11): the slug, the code, the address and, at most, a reason
    /// of 500 characters or a start. A character of a reason is up to four bytes in UTF-8 and up to six once JSON escapes it, so
    /// the 8 KB leave room for the longest honest body and stop anything larger before it is read.
    /// </summary>
    public const int ManageMaxBodyBytes = 8 * 1024;

    /// <summary>Longest token accepted from a link (the real one is 43 characters).</summary>
    public const int TokenMaxLength = 128;

    /// <summary>
    /// Holds a supplier's expiry run deletes in one pass: a run that stays short is better than one that holds a lock for
    /// minutes, and the next run (5 minutes later) goes on.
    /// </summary>
    public const int ExpiryBatchSize = 500;

    /// <summary>
    /// Customers and requests one run of the retention job anonymizes at most: the first run after the period is configured
    /// may find many, and the next night goes on.
    /// </summary>
    public const int RetentionBatchSize = 500;

    /// <summary>
    /// Random codes tried before a booking gives up finding a free public code (<see cref="Services.BookingCodes"/>, 50 bits:
    /// one try is enough in practice, the others only cover a collision).
    /// </summary>
    public const int PublicCodeAttempts = 8;
}

/// <summary>The languages the e-mails to a customer are written in (<c>ServiceCustomer.Locale</c>).</summary>
public static class ServiceCustomerLocales
{
    public const string Italian = "it";
    public const string English = "en";

    /// <summary>The language of a customer who did not say one, or said one CasaZen does not write in.</summary>
    public const string Default = Italian;

    /// <summary>The two-letter language of <paramref name="value"/> (<c>en</c>, <c>EN</c>, <c>en-GB</c>), or <see cref="Default"/>.</summary>
    public static string Normalize(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length < ShowcaseBookingLimits.LocaleLength)
            return Default;

        var language = text[..ShowcaseBookingLimits.LocaleLength].ToLowerInvariant();
        return language is Italian or English && (text.Length == ShowcaseBookingLimits.LocaleLength || text[2] is '-' or '_')
            ? language
            : Default;
    }
}
