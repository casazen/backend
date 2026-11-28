namespace Casazen.Infrastructure.OTA;

public class AirbnbAvailabilityRequest
{
    public string ListingId { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public bool Available { get; set; }
}

public class AirbnbPricingRequest
{
    public string ListingId { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "EUR";
}

public class AirbnbApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Error { get; set; }
}

public class AirbnbReservationsResponse
{
    public List<AirbnbReservation> Reservations { get; set; } = new();
}

public class AirbnbReservation
{
    public string ConfirmationCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CheckIn { get; set; }
    public DateTime CheckOut { get; set; }
    public decimal TotalPrice { get; set; }
    public string Currency { get; set; } = "EUR";
    public AirbnbGuest Guest { get; set; } = new();
}

public class AirbnbGuest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
}

public class AirbnbListing
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
