using Casazen.Core.SiteProfile;

namespace Casazen.Core.Services;

/// <summary>
/// The site profile of a property (DB-11a): the optional facts and texts the property page of the booking site shows when the host
/// filled them in. Reads and writes one property of the caller's org (the tenant filter hides another org's property, so it is simply
/// not found); the permission to do so is the caller's to check (<c>PropertySiteProfileController</c>).
/// </summary>
public interface IPropertySiteProfileService
{
    /// <summary>The profile as the host editor shows it; <c>null</c> when the property does not exist for the caller.</summary>
    Task<SitePropertyProfileView?> GetAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the profile with <paramref name="input"/> (whatever it leaves out is cleared) and answers with what is stored;
    /// <c>null</c> when the property does not exist for the caller.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 <c>property_not_bookable_in_long_mode</c> (a long-rent property has no page on the booking site) or one of
    /// <see cref="SitePropertyProfileErrorCodes"/> with the <c>field</c> that broke the rule. Nothing is saved.
    /// </exception>
    Task<SitePropertyProfileView?> SaveAsync(Guid propertyId, SitePropertyProfileInput input, CancellationToken cancellationToken = default);
}
