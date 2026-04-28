namespace Casazen.Core.Services;

public interface ITaxCalculationService
{
    Task<decimal> CalculateTouristTaxAsync(Guid propertyId, DateTime checkIn, DateTime checkOut, int numberOfGuests);
}
