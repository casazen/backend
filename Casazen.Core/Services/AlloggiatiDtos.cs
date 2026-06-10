using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public record AlloggiatiStatusInfo(
    Guid BookingId,
    AlloggiatiWebStatus Status,
    string? ConfirmationNumber,
    string? ErrorMessage,
    DateTime? ReportedAt,
    double HoursUntilDeadline,
    bool IsOverdue,
    bool DataComplete);

public record AlloggiatiSummaryInfo(
    Guid BookingId,
    string GuestName,
    string PropertyName,
    DateTime CheckInDate,
    AlloggiatiWebStatus Status,
    bool DataComplete,
    bool IsOverdue,
    double HoursUntilDeadline);
