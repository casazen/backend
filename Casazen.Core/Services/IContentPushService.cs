namespace Casazen.Core.Services;

/// <summary>
/// Publishes a property to Octorate and to the channels that receive content (Booking.com, Airbnb, Vrbo, HomeToGo, Holidu: NF-27) and records the
/// result for each channel (E02/F03/US01/T02, ADR-027, ADR-034, ADR-036). Calendar, prices and restrictions are not sent here: that is E08/F05,
/// which consumes the <c>ListingPublished</c> event.
/// </summary>
public interface IContentPushService
{
    /// <summary>
    /// Checks the guards and queues the publication (the job <c>ListingPublishJob</c>, one run for each property at a time); answers with the
    /// channel rows now <c>Sending</c>. The guards, before anything is queued: the property is of the caller's org and within their reach (404),
    /// every channel is a content channel (422 <c>listing_channel_unknown</c>), the property is in no proposed or ambiguous mapping (409
    /// <c>octorate_mapping_unconfirmed</c>) and, without a confirmed mapping, was declared <c>newInventory</c> (409
    /// <c>octorate_existing_inventory_must_import</c>), the Italian and English texts exist (422 <c>listing_content_incomplete</c> with the missing
    /// ones), and no channel of the property has a change made outside CasaZen unless <paramref name="overwrite"/> is true (409
    /// <c>listing_modified_outside</c> with the channels flagged, requested or not: the content is shared by the channels and a publication writes
    /// it for all of them): nothing is overwritten silently (NF-27). A list of channels that is empty is refused (422), never read as «all».
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="channels">The codes of the portals to publish to.</param>
    /// <param name="overwrite">The person's choice to write CasaZen's version over a change made outside CasaZen.</param>
    /// <param name="userId">The person who publishes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="tokenRoles">The roles of the token of <paramref name="userId"/> (see <see cref="IOctoratePropertyCreator.CreateAsync"/>).</param>
    Task<IReadOnlyList<ListingChannelResult>> PublishAsync(
        Guid propertyId,
        IReadOnlyList<string> channels,
        bool overwrite,
        string userId,
        CancellationToken ct,
        IReadOnlySet<string>? tokenRoles = null);

    /// <summary>
    /// Runs the publication the job was queued for (called by <c>ListingPublishJob</c> under the org of the property): creates the property on Octorate
    /// when it is new, pushes the groups of the content, the photos and each channel, and records the result. A channel whose rule is not met, or
    /// that fails, is <c>ToFix</c> with its code and a «To do», and the others go on (AC-NF29-3). The run is for the channels it was queued for,
    /// whatever state their rows are in (they are put in <c>Sending</c> first): a publication queued while an earlier run of the property was going
    /// on is not lost.
    /// </summary>
    Task RunAsync(
        Guid propertyId,
        IReadOnlyList<string> channels,
        bool overwrite,
        string userId,
        IReadOnlySet<string>? tokenRoles,
        CancellationToken ct);
}
