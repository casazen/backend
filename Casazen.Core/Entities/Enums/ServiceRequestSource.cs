namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a service request comes from (SP-10). Stored as an integer in <c>ServiceRequests.Source</c>: never reorder or
/// renumber, only append. It always agrees with <see cref="ServiceRequestRentalContext"/> (a database check keeps them
/// together): <see cref="Showcase"/> is the one of <see cref="ServiceRequestRentalContext.Showcase"/>, <see cref="Host"/> the one
/// of the short-rent and long-rent contexts. The supplier console shows it as <c>source</c> (<c>casazen</c> / <c>showcase</c>).
/// </summary>
public enum ServiceRequestSource
{
    /// <summary>A CasaZen host asked the supplier for the work (short-rent or long-rent context).</summary>
    Host = 0,

    /// <summary>A customer without an account booked it from the supplier's public showcase (SP-10).</summary>
    Showcase = 1,
}
