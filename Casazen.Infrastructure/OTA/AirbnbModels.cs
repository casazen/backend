namespace Casazen.Infrastructure.OTA;

/// <summary>
/// Request model for updating calendar availability on Airbnb
/// </summary>
public class AirbnbAvailabilityRequest
{
    public string ListingId { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public bool Available { get; set; }
}

/// <summary>
/// Request model for updating pricing on Airbnb
/// </summary>
public class AirbnbPricingRequest
{
    public string ListingId { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "EUR";
}

/// <summary>
/// Response model for Airbnb API calls
/// </summary>
public class AirbnbApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Airbnb reservation response model
/// </summary>
public class AirbnbReservationsResponse
{
    public List<AirbnbReservation> Reservations { get; set; } = new();
}

/// <summary>
/// Airbnb reservation model
/// </summary>
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

/// <summary>
/// Airbnb guest model
/// </summary>
public class AirbnbGuest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
}

/// <summary>
/// Airbnb listing model
/// </summary>
public class AirbnbListing
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
