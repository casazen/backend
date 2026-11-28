using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory.GuestRegistration;

namespace Casazen.Core.Services;

/// <summary>
/// The one place that decides which regime of the registration of the guests a property is under (E29/F02/US01/T01, NF-125, decision D3): the country, for a
/// Swiss property the canton the place resolver gives (<see cref="IPropertySwissPlaceResolver"/>, E29/F01), then the catalog
/// (<see cref="GuestRegistrationRegimeCatalog"/>). No other service tests «country is CH and the canton is TI» for this: they ask here (the Ticino code of
/// PR #997 reads the same canton from the same place resolver, so the two never disagree).
/// </summary>
/// <remarks>
/// <para><b>What it answers.</b> The regime, the place, the host's setting, the duty in effect (<see cref="GuestRegistrationDutyRules"/>) and what the property
/// still lacks (<see cref="GuestRegistrationReadiness"/>). A Swiss property whose municipality is unknown or ambiguous is under
/// <see cref="GuestRegistrationRegimeCatalog.UnknownSwissPlace"/>: the resolver never chooses a canton silently.</para>
/// <para><b>Read only, and the flag.</b> Resolving never writes. While <c>Features:GuestRegistrationRegimes</c> is off the host's setting is <b>not read</b>:
/// <see cref="GuestRegistrationResolution.Override"/> is <c>null</c> and the duty is the regime's own, whatever the column holds.</para>
/// <para><b>Tenant.</b> The property is read through the tenant filter of the context: another org's property is not found.</para>
/// </remarks>
public interface IGuestRegistrationRegimeResolver
{
    Task<GuestRegistrationResolution> ResolveAsync(Guid propertyId, CancellationToken cancellationToken = default);

    Task<GuestRegistrationResolution> ResolveAsync(Property property, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, GuestRegistrationResolution>> ResolveManyAsync(
        IReadOnlyCollection<Property> properties,
        CancellationToken cancellationToken = default);
}

/// <summary>What the resolver says of a property.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Regime">The regime of its place; <c>null</c> for a property in a country that is neither Italy nor Switzerland (the catalog has nothing for it).</param>
/// <param name="Place">Where the place resolver puts the property: <c>null</c> outside Switzerland, <see cref="SwissPlace.Ambiguous"/> when the postal code leaves several municipalities open.</param>
/// <param name="Override">The host's setting; <c>null</c> = follow the regime, and always <c>null</c> while the flag is off.</param>
/// <param name="EffectiveDuty">The duty CasaZen treats as in effect: the setting when there is one, else the default of the regime.</param>
/// <param name="IdentifierMissing">The reasons the property cannot be notified yet (<see cref="GuestRegistrationReadiness"/>); empty when nothing is missing.</param>
public sealed record GuestRegistrationResolution(
    Guid PropertyId,
    GuestRegistrationRegime? Regime,
    SwissPlace? Place,
    GuestNotificationOverride? Override,
    GuestRegistrationEffectiveDuty EffectiveDuty,
    IReadOnlyList<string> IdentifierMissing);

public interface IGuestNotificationOverrideService
{
    Task SetAsync(Guid propertyId, GuestNotificationOverride? value, string userId, CancellationToken cancellationToken = default);
}

public static class GuestRegistrationErrorCodes
{
    /// <summary>422: the setting is for a property in Switzerland; an Italian one follows Alloggiati Web and nothing the host says changes that.</summary>
    public const string OverrideNotApplicable = "guest_registration_override_not_applicable";

    public const string OverrideNotApplicableMessageKey = "GuestRegistrationOverrideNotApplicable";

    public const string CountryNotSupported = "guest_registration_country_not_supported";

    public const string CountryNotSupportedMessageKey = "GuestRegistrationCountryNotSupported";

    public const string OverrideRequiredMessageKey = "GuestRegistrationOverrideRequired";
}
