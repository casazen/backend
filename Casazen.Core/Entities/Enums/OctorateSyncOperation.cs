namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The last operation CasaZen sent to Octorate for a booking (<see cref="OctorateBookingSync.LastOperation"/>, E08/F01/US01/T01). Stored as its
/// number and append-only.
/// </summary>
public enum OctorateSyncOperation
{
    /// <summary>The reservation is created on Octorate.</summary>
    Create = 1,

    /// <summary>The dates, the guests, the price or the notes of the reservation are changed.</summary>
    Update = 2,

    /// <summary>The reservation is cancelled.</summary>
    Cancel = 3,

    /// <summary>A payment of the booking is recorded on the reservation (income, never a card charge).</summary>
    Payment = 4,
}
