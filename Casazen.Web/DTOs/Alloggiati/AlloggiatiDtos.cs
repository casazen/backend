using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.Alloggiati;

public class AlloggiatiStatusDto
{
    public Guid BookingId { get; set; }
    public AlloggiatiWebStatus Status { get; set; }
    public string? ConfirmationNumber { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? ReportedAt { get; set; }
    public double HoursUntilDeadline { get; set; }
    public bool IsOverdue { get; set; }
    public bool DataComplete { get; set; }
}

public class AlloggiatiSummaryDto
{
    public Guid BookingId { get; set; }
    public string GuestName { get; set; } = string.Empty;
    public string PropertyName { get; set; } = string.Empty;
    public DateTime CheckInDate { get; set; }
    public AlloggiatiWebStatus Status { get; set; }
    public bool DataComplete { get; set; }
    public bool IsOverdue { get; set; }
    public double HoursUntilDeadline { get; set; }
}
