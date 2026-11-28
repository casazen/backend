namespace Casazen.Core.Suppliers;

public static class ShowcaseBookingLimits
{
    public const int FullNameMaxLength = 100;

    public const int EmailMaxLength = 254;

    public const int PhoneMinDigits = 6;

    public const int PhoneMaxDigits = 15;

    public const int CityMaxLength = 100;

    public const int PostalCodeLength = 5;

    public const int AddressMaxLength = 200;

    public const int FloorMaxLength = 60;

    public const int AccessNotesMaxLength = 500;

    public const int PrivacyNoticeVersionMaxLength = 50;

    public const int ConsentIpMaxLength = 45;

    public const int LocaleLength = 2;

    /// <summary>
    /// Bookings of one e-mail address, for one supplier, that may wait for the e-mail check at the same time (decision of
    /// <c>gap/05</c> §4.2): the fourth is refused until one is checked or lapses. It keeps one address from holding many slots.
    /// </summary>
    public const int MaxUnverifiedHoldsPerEmail = 3;

    public const int SlotWindowBehindDays = 1;

    public const int SlotWindowAheadDays = SupplierAgendaLimits.MaxHorizonDays + 2;

    public const int CreateMaxBodyBytes = 16 * 1024;

    public const int ConfirmMaxBodyBytes = 1024;

    public const int ManageMaxBodyBytes = 8 * 1024;

    public const int TokenMaxLength = 128;

    public const int ExpiryBatchSize = 500;

    public const int RetentionBatchSize = 500;

    public const int PublicCodeAttempts = 8;
}

public static class ServiceCustomerLocales
{
    public const string Italian = "it";
    public const string English = "en";

    public const string Default = Italian;

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
