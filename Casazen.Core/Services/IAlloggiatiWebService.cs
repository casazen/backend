using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IAlloggiatiWebService
{
    Task ReportGuestAsync(Guid guestId, Guid bookingId);
    Task<bool> ValidateGuestDataAsync(Guid guestId);
    Task<AlloggiatiWebReport?> GetReportStatusAsync(Guid bookingId);
    Task<AlloggiatiStatusInfo> GetStatusAsync(Guid bookingId);
    Task<IReadOnlyList<AlloggiatiSummaryInfo>> GetSummaryAsync(Guid? propertyId);
    Task<AlloggiatiStatusInfo> SendManualAsync(Guid bookingId);
    double GetHoursUntilDeadline(DateTime checkInDate);
    bool IsOverdue(DateTime checkInDate, bool dataComplete, AlloggiatiWebStatus? reportStatus);
}
