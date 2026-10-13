namespace Casazen.Core.Services;

/// <summary>
/// Who is the holder (<i>titolare</i>) of an org (AM-03, S5): the person who may do in the org's name what the law ties to the
/// landlord, like the RLI delega and the IMU communication. With a team that is no longer «whoever created the property»
/// (<c>Property.OwnerId</c> is just the creator, and a property manager creates properties too): it is the org's owner and its
/// administrators.
/// </summary>
public interface IOrgHolderService
{
    /// <summary>
    /// True when <paramref name="userId"/> is the owner or an active administrator of <paramref name="orgId"/>. Read from the
    /// database on every call, never from the authorization cache: it guards acts that bind the landlord.
    /// </summary>
    /// <param name="propertyCreatorId">
    /// The creator of the property the act is about. It decides only for an account that is in <b>no</b> org team (an owner of
    /// before the team, a test account): there the creator is the landlord, as it has always been. A member of a team is never
    /// the holder because it created a property.
    /// </param>
    Task<bool> IsHolderAsync(
        string userId,
        Guid orgId,
        string propertyCreatorId,
        CancellationToken cancellationToken = default);
}
