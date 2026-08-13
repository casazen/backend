using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IGuestCheckInService
{
    /// <summary>Creates a new session, returns the raw (unhashed) token.</summary>
    Task<string> CreateSessionAsync(Guid bookingId, Guid orgId);

    /// <summary>Returns the active session matching the raw token, or null if expired/invalid.</summary>
    Task<GuestCheckInSession?> GetSessionByTokenAsync(string token);

    /// <summary>Returns the most recent active session for a booking (for host view).</summary>
    Task<GuestCheckInSession?> GetSessionForBookingAsync(Guid bookingId);

    /// <summary>Submit guest data. Returns result indicating success or duplicate.</summary>
    Task<GuestCheckInSubmitResult> SubmitAsync(string token, GuestCheckInSubmitRequest request);

    /// <summary>Marks the session as AlloggiatiInviato after the job is enqueued.</summary>
    Task MarkAlloggiatiEnqueuedAsync(Guid sessionId);

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

public class GuestCheckInSubmitResult
{
    public bool Success { get; set; }
    public bool Duplicate { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? BookingId { get; set; }
    public Guid? GuestId { get; set; }
}
