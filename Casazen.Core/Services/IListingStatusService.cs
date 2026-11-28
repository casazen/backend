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

    Task<IReadOnlyList<ListingPropertySummary>> GetSummaryAsync(HostScope scope, CancellationToken ct);

    Task<int> SyncPropertyAsync(Guid orgId, Guid propertyId, CancellationToken ct);
}
