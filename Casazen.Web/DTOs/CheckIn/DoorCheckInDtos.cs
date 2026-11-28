using Casazen.Core.Services;

namespace Casazen.Web.DTOs.CheckIn;

public sealed class DoorCheckInRequest
{
    public string? BookingCode { get; set; }

    public string? Surname { get; set; }

    public DateOnly? ArrivalDate { get; set; }

    public DoorCheckInInput ToInput() => new(BookingCode, Surname, ArrivalDate);
}

public sealed class DoorCheckInResponse
{
    public string Token { get; set; } = string.Empty;
}

public sealed class DoorQrDto
{
    public string Url { get; set; } = string.Empty;
}
