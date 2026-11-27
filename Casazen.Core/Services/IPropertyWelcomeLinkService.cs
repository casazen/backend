using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The welcome link of a property (E07/F04/US01/T03, NF-90, CRM-03): the address behind the QR code printed in the house, where a guest leaves
/// a contact and the consents for it (<see cref="IWelcomeConsentService"/>). A property has <b>one active link</b> at a time. The token is
/// 192 random bits in base64url, carries nothing about the property and is unique in the whole table, so a code never reaches another
/// property. Rotating revokes the active link and makes the new one in the same unit of work: the code printed on the old one stops working at
/// once, and the revoked row stays in the table so that its token is never given to another property.
/// </summary>
/// <remarks>
/// Every method names the org, which the web layer took from the property the caller may write (<c>property.write</c>, tenant and HostScope):
/// services never check roles. A property that is not in the org (or is deleted) is a <see cref="Exceptions.NotFoundException"/>.
/// </remarks>
public interface IPropertyWelcomeLinkService
{
    /// <summary>The active link of the property, or <c>null</c> when it has none.</summary>
    Task<PropertyWelcomeLink?> GetActiveAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active link of the property; when there is none, a new one is made. Two callers racing to make the first link end with one: the
    /// database refuses the second row and the second caller gets the first one.
    /// </summary>
    /// <param name="createdByUserId">The user (Auth0 subject) who asked, <c>null</c> when the system did.</param>
    Task<PropertyWelcomeLinkResult> CreateOrGetAsync(
        Guid orgId,
        Guid propertyId,
        string? createdByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the active link, if any, and makes a new one with a new token, in one transaction. The new link is the property's only active
    /// one; the old token answers like a token that never existed.
    /// </summary>
    Task<PropertyWelcomeLink> RotateAsync(
        Guid orgId,
        Guid propertyId,
        string? createdByUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>An active welcome link and whether the call just made it.</summary>
/// <param name="Link">The active link.</param>
/// <param name="Created"><c>true</c> when this call made it, <c>false</c> when the property already had it.</param>
public sealed record PropertyWelcomeLinkResult(PropertyWelcomeLink Link, bool Created);
