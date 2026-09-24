namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The rental context a service request was opened in (decision D2, SU-07). It decides what the request is tied to and
/// which host endpoints reach it: <c>api/service-requests</c> (short-rent context) or
/// <c>api/long-rent/service-requests</c> (long-rent context). Stored as an integer: append only.
/// </summary>
public enum ServiceRequestRentalContext
{
    /// <summary>
    /// Short-term rental: tied to one stay (<see cref="ServiceRequest.BookingId"/> required at creation, a booking of the
    /// same property and org). Requests created before SU-07 that could not be traced to a single stay keep a null
    /// <c>BookingId</c> and stay on their property (migration <c>AddServiceRequestRentalContext</c>).
    /// </summary>
    ShortRent = 0,

    /// <summary>Long-term rental: tied to the property only, never to a booking.</summary>
    LongRent = 1,
}
