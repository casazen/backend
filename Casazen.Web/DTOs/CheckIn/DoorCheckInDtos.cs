using Casazen.Core.Services;

namespace Casazen.Web.DTOs.CheckIn;

/// <summary>
/// What the guest typed at the door (<c>POST api/public/checkin/door/{doorToken}</c>, E22/F01/US02/T01): the booking code (the code of the confirmation
/// e-mail) or, when it has none, the surname of the head guest, and the arrival date (<c>yyyy-MM-dd</c>). The code wins when both are sent.
/// </summary>
public sealed class DoorCheckInRequest
{
    public string? BookingCode { get; set; }

    public string? Surname { get; set; }

    public DateOnly? ArrivalDate { get; set; }

    public DoorCheckInInput ToInput() => new(BookingCode, Surname, ArrivalDate);
}

/// <summary>
/// The answer at the door: the token of the check-in link of the stay and nothing else, no data of the booking. The page opens <c>/checkin/{token}</c>, the same page
/// as the link of the e-mail, at the identity step.
/// </summary>
public sealed class DoorCheckInResponse
{
    public string Token { get; set; } = string.Empty;
}

/// <summary>The address behind the QR code of the door of a property (<c>GET api/properties/{id}/door-qr</c>): <c>{App:PublicSiteBaseUrl}/checkin/porta/{doorToken}</c>.</summary>
public sealed class DoorQrDto
{
    public string Url { get; set; } = string.Empty;
}
