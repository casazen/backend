using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IBookingRepository
{
    Task<Booking?> GetByIdAsync(Guid id);
    Task<IEnumerable<Booking>> GetByPropertyAsync(Guid propertyId);
    Task<IEnumerable<Booking>> GetByGuestAsync(Guid guestId);
    Task<IEnumerable<Booking>> GetAllAsync();
    Task<IEnumerable<Booking>> GetByDateRangeAsync(Guid propertyId, DateTime startDate, DateTime endDate);
    Task<bool> IsAvailableAsync(Guid propertyId, DateTime checkIn, DateTime checkOut);
    Task<Booking> AddAsync(Booking booking);
    Task<Booking> UpdateAsync(Booking booking);
    Task DeleteAsync(Guid id);
}