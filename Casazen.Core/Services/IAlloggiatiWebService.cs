using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IAlloggiatiWebService
{
    Task ReportGuestAsync(Guid guestId, Guid bookingId);
    Task<bool> ValidateGuestDataAsync(Guid guestId);
    Task<AlloggiatiWebReport?> GetReportStatusAsync(Guid bookingId);
}
