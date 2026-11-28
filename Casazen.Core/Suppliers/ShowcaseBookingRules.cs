using System.Net.Mail;
using System.Text;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;

namespace Casazen.Core.Suppliers;

/// <summary>
/// What an anonymous customer sends to book a supplier from its showcase, as read from the request: nothing is checked yet
/// (<see cref="ShowcaseBookingRules.Normalize"/> checks it). The estimate fields (<paramref name="Quantity"/>,
/// <paramref name="SurfaceSqm"/>, <paramref name="Options"/>) are those of <see cref="SupplierQuoteRequest"/>.
/// </summary>
/// <param name="ClientRequestId">The id the client made up for this attempt: the same id is the same booking.</param>
/// <param name="Service">The slug of the published service.</param>
/// <param name="StartUtc">The slot the customer picked: its start, as <c>GET …/slots</c> gave it.</param>
/// <param name="ComuneIstat">ISTAT code of the comune, when the customer chose it from the official list.</param>
/// <param name="City">The comune where the work is.</param>
/// <param name="PrivacyAccepted">The customer accepted the privacy notice.</param>
/// <param name="PrivacyNoticeVersion">The version of the notice the page showed (it has to be the current one).</param>
/// <param name="ConsentIp">The client address, as the server saw it (not sent by the client): consent evidence.</param>
/// <param name="Urgent">
/// The slot the customer picked is an urgent one (SP-04c, <c>GET …/slots?urgent=true</c>): the booking is judged against the urgent
/// plan under the calendar lock, priced with the surcharge line, and the request is born <c>Urgency = High</c>. <c>false</c> is the
/// booking every client made before.
/// </param>
public sealed record ShowcaseBookingInput(
    Guid ClientRequestId,
    string? Service,
    DateTime? StartUtc,
    int? Quantity,
    int? SurfaceSqm,
    IReadOnlyList<SupplierQuoteOption?>? Options,
    string? ComuneIstat,
    string? City,
    string? PostalCode,
    string? Address,
    string? Floor,
    string? AccessNotes,
    string? FullName,
    string? Email,
    string? Phone,
    string? Locale,
    bool PrivacyAccepted,
    string? PrivacyNoticeVersion,
    string? ConsentIp,
    bool Urgent = false);

public sealed record ShowcaseBookingContent(
    Guid ClientRequestId,
    string ServiceSlug,
    DateTime StartUtc,
    SupplierQuoteRequest Quote,
    string? ComuneIstat,
    string City,
    string PostalCode,
    string Address,
    string? Floor,
    string? AccessNotes,
    string FullName,
    string Email,
    string Phone,
    string Locale,
    string PrivacyNoticeVersion,
    string ConsentIp);

public static class ShowcaseBookingRules
{
    /// <summary>
    /// Whether <paramref name="startUtc"/> is a time the slot planner could offer at <paramref name="nowUtc"/> at all: not further
    /// back than <see cref="ShowcaseBookingLimits.SlotWindowBehindDays"/> and not further ahead than
    /// <see cref="ShowcaseBookingLimits.SlotWindowAheadDays"/>. It does not say the slot is free (the planner does, after the lock);
    /// it keeps a date like 0001-01-01 or 9999-12-31, which anyone can send, out of the date arithmetic of the planner.
    /// </summary>
    public static bool IsWithinSlotWindow(DateTime startUtc, DateTime nowUtc) =>
        startUtc >= nowUtc.AddDays(-ShowcaseBookingLimits.SlotWindowBehindDays)
        && startUtc <= nowUtc.AddDays(ShowcaseBookingLimits.SlotWindowAheadDays);

    public static ShowcaseBookingContent Normalize(ShowcaseBookingInput input, string currentPrivacyVersion)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPrivacyVersion);

        var invalid = new List<string>();

        if (input.ClientRequestId == Guid.Empty)
            invalid.Add(ShowcaseBookingFields.ClientRequestId);

        var service = input.Service?.Trim();
        if (string.IsNullOrEmpty(service))
            invalid.Add(SupplierQuoteFields.Service);

        DateTime start = default;
        if (input.StartUtc is not { } requested)
        {
            invalid.Add(ShowcaseBookingFields.StartUtc);
        }
        else
        {
            start = UtcDateTime.Normalize(requested);
            if (start.Ticks % TimeSpan.TicksPerMinute != 0)
                invalid.Add(ShowcaseBookingFields.StartUtc);
        }

        var comuneIstat = input.ComuneIstat?.Trim();
        if (string.IsNullOrEmpty(comuneIstat))
            comuneIstat = null;
        else if (!ComuneRules.IsIstatCode(comuneIstat))
            invalid.Add(ShowcaseBookingFields.ComuneIstat);

        var city = Text(input.City, ShowcaseBookingLimits.CityMaxLength, required: true, ShowcaseBookingFields.City, invalid);
        var postalCode = input.PostalCode?.Trim();
        if (postalCode is not { Length: ShowcaseBookingLimits.PostalCodeLength } || !postalCode.All(c => c is >= '0' and <= '9'))
        {
            invalid.Add(ShowcaseBookingFields.PostalCode);
            postalCode = null;
        }

        var address = Text(input.Address, ShowcaseBookingLimits.AddressMaxLength, required: true, ShowcaseBookingFields.Address, invalid);
        var floor = Text(input.Floor, ShowcaseBookingLimits.FloorMaxLength, required: false, ShowcaseBookingFields.Floor, invalid);
        var accessNotes = Notes(input.AccessNotes, invalid);

        var fullName = FullName(input.FullName, invalid);
        var email = Email(input.Email, invalid);
        var phone = Phone(input.Phone, invalid);

        if (invalid.Count > 0)
            throw ShowcaseBookingErrors.InvalidFields(invalid);

        if (!input.PrivacyAccepted)
            throw ShowcaseBookingErrors.NoConsent();

        var version = input.PrivacyNoticeVersion?.Trim();
        if (!string.Equals(version, currentPrivacyVersion.Trim(), StringComparison.Ordinal))
            throw ShowcaseBookingErrors.OldConsent();

        return new ShowcaseBookingContent(
            input.ClientRequestId,
            service!,
            start,
            new SupplierQuoteRequest(input.Quantity, input.SurfaceSqm, input.Options, comuneIstat ?? city, postalCode, input.Urgent),
            comuneIstat,
            city!,
            postalCode!,
            address!,
            floor,
            accessNotes,
            fullName!,
            email!,
            phone!,
            ServiceCustomerLocales.Normalize(input.Locale),
            version!,
            Truncate(input.ConsentIp, ShowcaseBookingLimits.ConsentIpMaxLength));
    }

    public static string NormalizeEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }

    public static string AbbreviateName(string? fullName)
    {
        var parts = (fullName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return string.Empty;
        if (parts.Length == 1)
            return parts[0];

        var initial = FirstLetter(parts[^1]);
        return initial.Length == 0 ? parts[0] : $"{parts[0]} {initial}.";
    }

    private static string FirstLetter(string word)
    {
        foreach (var rune in word.EnumerateRunes())
        {
            if (Rune.IsLetter(rune))
                return Rune.ToUpperInvariant(rune).ToString();
        }

        return string.Empty;
    }

    internal static string? Text(string? value, int maxLength, bool required, string field, List<string> invalid)
    {
        var text = Collapse(value);
        if (text.Length == 0)
        {
            if (required)
                invalid.Add(field);
            return null;
        }

        if (text.Length > maxLength || text.Any(char.IsControl))
        {
            invalid.Add(field);
            return null;
        }

        return text;
    }

    private static string? Notes(string? value, List<string> invalid)
    {
        var text = value?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.Length > ShowcaseBookingLimits.AccessNotesMaxLength || text.Any(c => char.IsControl(c) && c != '\n' && c != '\t'))
        {
            invalid.Add(ShowcaseBookingFields.AccessNotes);
            return null;
        }

        return text;
    }

    internal static string? FullName(string? value, List<string> invalid)
    {
        var name = Collapse(value);
        if (name.Length < 2 || name.Length > ShowcaseBookingLimits.FullNameMaxLength || name.Any(char.IsControl) || !name.Any(char.IsLetter))
        {
            invalid.Add(ShowcaseBookingFields.FullName);
            return null;
        }

        return name;
    }

    internal static string? Email(string? value, List<string> invalid)
    {
        var email = value?.Trim();
        if (string.IsNullOrEmpty(email)
            || email.Length > ShowcaseBookingLimits.EmailMaxLength
            || email.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !MailAddress.TryCreate(email, out var parsed)
            || !string.IsNullOrEmpty(parsed.DisplayName)
            || !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase)
            || !parsed.Host.Contains('.', StringComparison.Ordinal))
        {
            invalid.Add(ShowcaseBookingFields.Email);
            return null;
        }

        return email;
    }

    internal static string? Phone(string? value, List<string> invalid)
    {
        var text = value?.Trim();
        var plus = text is not null && text.StartsWith('+');
        var digits = new string((text ?? string.Empty).Where(c => c is >= '0' and <= '9').ToArray());

        var onlyPhoneCharacters = text is not null && text.All(c => c is >= '0' and <= '9' or ' ' or '.' or '-' or '(' or ')' or '/' or '+');
        var plusOnlyFirst = text is not null && text.LastIndexOf('+') <= 0;
        if (!onlyPhoneCharacters
            || !plusOnlyFirst
            || digits.Length < ShowcaseBookingLimits.PhoneMinDigits
            || digits.Length > ShowcaseBookingLimits.PhoneMaxDigits)
        {
            invalid.Add(ShowcaseBookingFields.Phone);
            return null;
        }

        return plus ? "+" + digits : digits;
    }

    private static string Collapse(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    internal static string Truncate(string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
