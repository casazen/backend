using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Manages internal activities performed by staff (collaborators) on properties (NF-22).
/// Activities are isolated by perimeter: staff sees only activities on properties they can access.
/// </summary>
public interface IInternalActivityService
{
    /// <summary>
    /// Creates a new internal activity (cleaning, maintenance, check-in assistance).
    /// </summary>
    /// <param name="propertyId">The property where the activity will be performed.</param>
    /// <param name="staffUserId">The staff user assigned to the activity.</param>
    /// <param name="activityType">Type of activity (Cleaning, Maintenance, CheckIn).</param>
    /// <param name="scheduledAt">When the activity is scheduled (UTC).</param>
    /// <param name="internalRate">Optional internal rate per activity or per person.</param>
    /// <param name="rechargeToOwner">Whether the cost should be recharged to the owner.</param>
    /// <param name="bookingId">Optional booking this activity is related to.</param>
    /// <param name="notes">Optional notes about the activity.</param>
    /// <param name="createdByUserId">Who is creating the activity.</param>
    /// <returns>The created activity.</returns>
    Task<InternalActivity> CreateActivityAsync(
        Guid propertyId,
        string staffUserId,
        string activityType,
        DateTime scheduledAt,
        decimal? internalRate = null,
        bool rechargeToOwner = false,
        Guid? bookingId = null,
        string? notes = null,
        string? createdByUserId = null);

    /// <summary>
    /// Updates the status of an activity and optionally records completion.
    /// </summary>
    /// <param name="activityId">The activity to update.</param>
    /// <param name="status">The new status (Pending, InProgress, Completed).</param>
    /// <param name="notes">Optional notes to append or update.</param>
    /// <returns>The updated activity.</returns>
    Task<InternalActivity> UpdateStatusAsync(Guid activityId, string status, string? notes = null);

    /// <summary>
    /// Gets all activities within the caller's perimeter (filtered by HostScope).
    /// Staff with "Only some" perimeter sees only activities on their assigned properties.
    /// </summary>
    /// <param name="scope">The caller's property scope.</param>
    /// <param name="propertyId">Optional property filter.</param>
    /// <param name="staffUserId">Optional staff user filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activities the caller can see.</returns>
    Task<IReadOnlyList<InternalActivity>> GetActivitiesAsync(
        HostScope scope,
        Guid? propertyId = null,
        string? staffUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets activities assigned to the calling staff user.
    /// </summary>
    /// <param name="scope">The caller's property scope.</param>
    /// <param name="staffUserId">The staff user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activities assigned to this staff user within their perimeter.</returns>
    Task<IReadOnlyList<InternalActivity>> GetMyActivitiesAsync(
        HostScope scope,
        string staffUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single activity by ID.
    /// </summary>
    /// <param name="activityId">The activity ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The activity, or null if not found.</returns>
    Task<InternalActivity?> GetActivityAsync(Guid activityId, CancellationToken cancellationToken = default);
}
