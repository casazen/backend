namespace Casazen.Web.DTOs.Verification;

/// <summary>
/// A booking awaiting manual verification in the operator queue (NF-12, ADR-013).
/// </summary>
public class PendingVerificationDto
{
    public Guid BookingId { get; set; }
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public string GuestName { get; set; } = string.Empty;
    public string? GuestEmail { get; set; }
    public string? BiometricOutcome { get; set; }
    public int BiometricAttempts { get; set; }
    public DateTime RaisedAt { get; set; }
}
