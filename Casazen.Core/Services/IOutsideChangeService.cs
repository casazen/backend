namespace Casazen.Core.Services;

/// <summary>
/// Finds the changes made on Octorate after CasaZen's last push, so they are never overwritten without a choice (E02/F03/US01/T07, NF-27, ADR-027-R1,
/// R2, R4), and applies the person's choice. Detection re-reads the groups of the content and compares their hashes with the baseline stored after the
/// last accepted push; it never writes to Octorate.
/// </summary>
public interface IOutsideChangeService
{
    /// <summary>
    /// Re-reads the groups of the content of the property from the channel manager and compares them with the baseline of each channel row that
    /// was pushed (<c>Accepted</c> or <c>Published</c>): a difference sets <c>ExternalChangeAt</c> (when it is empty) and the groups that changed, and
    /// raises one «To do» for the property and channel. Returns the groups that changed (distinct). Never writes to Octorate.
    /// </summary>
    Task<IReadOnlyList<string>> DetectAsync(Guid orgId, Guid propertyId, CancellationToken ct);

    /// <summary>
    /// Applies the person's choice for one channel of the property. <see cref="OutsideChangeChoice.Keep"/> publishes the channel again with
    /// <c>overwrite</c>, which stores the new baseline and clears the change. <see cref="OutsideChangeChoice.Take"/> adopts the descriptions the channel
    /// manager holds (Italian into the columns of the property, the other languages into the site profile) and stores the new baseline; only the
    /// descriptions can be taken: for any other changed group it answers 422 <c>outside_change_take_not_supported</c> with the groups, and CasaZen keeps
    /// its version of them (<c>keep</c>). Both close the «To do». 404 when the property or the channel is not the caller's.
    /// </summary>
    Task ResolveAsync(
        Guid propertyId,
        string channelCode,
        OutsideChangeChoice choice,
        string userId,
        CancellationToken ct,
        IReadOnlySet<string>? tokenRoles = null);
}
