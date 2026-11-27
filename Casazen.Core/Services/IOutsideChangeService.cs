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
    /// raises one «To do» for the property and channel. Returns the groups that changed (distinct). Never writes to Octorate. A read that works
    /// also confirms the <c>Published</c> channels of the property (<c>LastSyncedAt</c>): nothing else reads them again, and a channel not
    /// confirmed for 24 hours is the one that went silent.
    /// </summary>
    Task<IReadOnlyList<string>> DetectAsync(Guid orgId, Guid propertyId, CancellationToken ct);

    Task ResolveAsync(
        Guid propertyId,
        string channelCode,
        OutsideChangeChoice choice,
        string userId,
        CancellationToken ct,
        IReadOnlySet<string>? tokenRoles = null);
}
