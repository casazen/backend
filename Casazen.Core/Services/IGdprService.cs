using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IGdprService
{
    /// <summary>
    /// Request data erasure for a guest (GDPR Article 17)
    /// </summary>
    Task<bool> RequestDataErasureAsync(Guid guestId, string reason);

    /// <summary>
    /// Anonymize guest data (execute erasure request)
    /// </summary>
    Task<bool> AnonymizeGuestDataAsync(Guid guestId);

    /// <summary>
    /// Get all pending erasure requests
    /// </summary>
    Task<IEnumerable<Guest>> GetPendingErasureRequestsAsync();

    /// <summary>
    /// Export guest data (GDPR Article 15 - Right to access)
    /// </summary>
    Task<GuestDataExport> ExportGuestDataAsync(Guid guestId);
}

public record GuestDataExport
{
    public Guid GuestId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public DateTime DataCollectedAt { get; init; }
    public List<BookingSummary> Bookings { get; init; } = new();
    public GdprConsents Consents { get; init; } = new();
}

public record BookingSummary
{
    public Guid BookingId { get; init; }
    public DateTime CheckInDate { get; init; }
    public DateTime CheckOutDate { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public decimal TotalPrice { get; init; }
}

public record GdprConsents
{
    public DateTime? DataProcessingConsentDate { get; init; }
    public DateTime? MarketingConsentDate { get; init; }
    public string ConsentIpAddress { get; init; } = string.Empty;
}
