using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IGuestCheckInService
{
    /// <summary>Creates a new session, returns the raw (unhashed) token.</summary>
    Task<string> CreateSessionAsync(Guid bookingId, Guid orgId);

    /// <summary>Returns the active session matching the raw token, or null if expired/invalid.</summary>
    Task<GuestCheckInSession?> GetSessionByTokenAsync(string token);

    /// <summary>
    /// What the public check-in portal may show for the raw token, or null if the token is unknown, expired or
    /// its booking is no longer eligible. A completed session exposes only its status (no booking data, no guest
    /// PII); an open one the booking context and a guest prefill whose document number is masked.
    /// </summary>
    Task<GuestCheckInPublicView?> GetPublicViewAsync(string token);

    /// <summary>Returns the most recent active session for a booking (for host view).</summary>
    Task<GuestCheckInSession?> GetSessionForBookingAsync(Guid bookingId);

    /// <summary>Submit guest data. Returns result indicating success or duplicate.</summary>
    Task<GuestCheckInSubmitResult> SubmitAsync(string token, GuestCheckInSubmitRequest request);

    /// <summary>Expires any active session for the booking and creates a fresh one.</summary>
    Task<string> RegenerateTokenAsync(Guid bookingId, Guid orgId);

    /// <summary>Expires the session matching the raw token.</summary>
    Task ExpireTokenAsync(string token);

    /// <summary>Expires active sessions for the booking except the session matching the raw token.</summary>
    Task ExpireOtherActiveSessionsAsync(Guid bookingId, string tokenToKeep);
}

public class GuestCheckInSubmitRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public string Nationality { get; set; } = string.Empty;
    public Gender? Gender { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public string DocumentIssuingCountry { get; set; } = string.Empty;
    public string PlaceOfBirth { get; set; } = string.Empty;
    public bool GdprConsent { get; set; }
    public bool MarketingConsent { get; set; }
    public string ConsentIpAddress { get; set; } = string.Empty;
}

/// <summary>
/// SharedResources keys of the public check-in validation messages (Italian and English texts in
/// <c>Casazen.Web/Resources/SharedResources*.resx</c>).
/// </summary>
public static class CheckInValidationKeys
{
    public const string FieldRequired = "CheckInFieldRequired";
    public const string FieldMaxLength = "CheckInFieldMaxLength";
    public const string GenderInvalid = "CheckInGenderInvalid";
    public const string DocumentTypeInvalid = "CheckInDocumentTypeInvalid";
    public const string GdprConsentRequired = "CheckInGdprConsentRequired";
}

public class GuestCheckInSubmitResult
{
    public bool Success { get; set; }
    public bool Duplicate { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? BookingId { get; set; }
    public Guid? GuestId { get; set; }

    /// <summary>Request property (C# name) the validation error refers to; null when the data is valid.</summary>
    public string? ValidationField { get; set; }

    /// <summary>SharedResources key of the validation message for <see cref="ValidationField"/>.</summary>
    public string? ValidationErrorKey { get; set; }
}

/// <summary>Public portal view of a check-in session (see <see cref="IGuestCheckInService.GetPublicViewAsync"/>).</summary>
public sealed class GuestCheckInPublicView
{
    public required GuestCheckInSessionStatus Status { get; init; }

    /// <summary>True once the guest has submitted the data: then every other property is null.</summary>
    public bool IsCompleted { get; init; }

    public Guid? SessionId { get; init; }
    public string? PropertyName { get; init; }
    public DateTime? CheckInDate { get; init; }
    public DateTime? CheckOutDate { get; init; }
    public GuestCheckInPrefill? GuestPrefill { get; init; }
}

/// <summary>Guest data already on file, shown to prefill the portal form before completion.</summary>
public sealed class GuestCheckInPrefill
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public DateTime? DateOfBirth { get; init; }
    public string Nationality { get; init; } = string.Empty;
    public Gender? Gender { get; init; }

    /// <summary>Document number with all but the last characters hidden, or null when none is on file.</summary>
    public string? DocumentNumberMasked { get; init; }

    public string DocumentIssuingCountry { get; init; } = string.Empty;
    public string PlaceOfBirth { get; init; } = string.Empty;
}
