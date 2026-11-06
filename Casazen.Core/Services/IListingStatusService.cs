using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// Tells the host, for each property and channel, what state its listing is in and why, and follows what Octorate does with a publication
/// (E02/F03/US01/T07, NF-09, NF-27, NF-30, NF-38, ADR-027, ADR-034). Reading is free of writes to Octorate; the follow-up reads the process of a
/// publication and the photo queue, and moves the rows to <c>Published</c> or <c>ToFix</c> with Octorate's reason.
/// </summary>
public interface IListingStatusService
{
    /// <summary>
    /// The channels of the property with their state, completeness, missing steps, last confirmation, lag, reason and the groups changed outside
    /// CasaZen; <c>null</c> when the property does not exist for the caller (another org's, or deleted). The completeness is worked out here from
    /// the rule of each channel (<c>ListingChannelOptions</c>), never stored.
    /// </summary>
    Task<ListingStatusView?> GetAsync(Guid propertyId, CancellationToken ct);

    /// <summary>The worst state and the sync alert of each property of the scope that has a channel, narrowed with <c>InScope</c> (a collaborator «Solo alcuni» sees only its properties).</summary>
    Task<IReadOnlyList<ListingPropertySummary>> GetSummaryAsync(HostScope scope, CancellationToken ct);

    /// <summary>
    /// Reads the publication process of each channel of the property waiting for it (<c>Sending</c> or <c>Accepted</c>) and the photo queue, and moves
    /// the rows and the photo tasks accordingly. Every successful read sets <c>LastSyncedAt</c>. A row that becomes <c>ToFix</c> gets one «To do»,
    /// closed when it recovers. Called by the job every 15 minutes and at once by the notification of the channel manager, for the org of the property.
    /// Returns how many rows and tasks changed.
    /// </summary>
    Task<int> SyncPropertyAsync(Guid orgId, Guid propertyId, CancellationToken ct);
}
