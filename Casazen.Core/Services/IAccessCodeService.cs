namespace Casazen.Core.Services;

/// <summary>
/// Manages property access codes with encryption and verification gating (NF-13, E06/F03/US01/T01).
/// Codes are encrypted at rest and NEVER released without verification.
/// </summary>
public interface IAccessCodeService
{
    /// <summary>
    /// Sets or updates the access code for a property (encrypted storage).
    /// </summary>
    /// <param name="propertyId">Property to set the code for</param>
    /// <param name="code">Plain text code (will be encrypted)</param>
    /// <param name="userId">User setting the code</param>
    /// <param name="ct">Cancellation token</param>
    Task SetAccessCodeAsync(Guid propertyId, string code, string userId, CancellationToken ct = default);

    /// <summary>
    /// Gets the access code for a booking (permission and verification required).
    /// Logs the access and BLOCKS if verification not passed.
    /// </summary>
    /// <param name="bookingId">Booking to get code for</param>
    /// <param name="userId">User requesting the code</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Plain text code, or null if not set or verification failed</returns>
    Task<string?> GetAccessCodeAsync(Guid bookingId, string userId, CancellationToken ct = default);

    /// <summary>
    /// Releases the access code to a guest (verification required, logged).
    /// Used by guest-facing endpoints after verification passes.
    /// </summary>
    /// <param name="bookingId">Booking to release code for</param>
    /// <param name="channel">How the code is released ("screen", "email")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Plain text code, or null if verification not passed</returns>
    Task<string?> ReleaseToGuestAsync(Guid bookingId, string channel, CancellationToken ct = default);
}
