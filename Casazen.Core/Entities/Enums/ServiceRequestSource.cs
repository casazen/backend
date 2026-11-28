namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a service request comes from (SP-10). Stored as an integer in <c>ServiceRequests.Source</c>: never reorder or
/// renumber, only append. It always agrees with <see cref="ServiceRequestRentalContext"/> (a database check keeps them
/// together): <see cref="Showcase"/> is the one of <see cref="ServiceRequestRentalContext.Showcase"/>, <see cref="Host"/> the one
/// of the short-rent and long-rent contexts. The supplier console shows it as <c>source</c> (<c>casazen</c> / <c>showcase</c>).
/// </summary>
public enum ServiceRequestSource
{
    Host = 0,

    Showcase = 1,
}
