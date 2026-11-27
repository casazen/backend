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
    /// <summary>The regime of the property <paramref name="propertyId"/> of the caller's org.</summary>
    /// <exception cref="Exceptions.NotFoundException"><c>property_not_found</c>: no such property in the org.</exception>
    Task<GuestRegistrationResolution> ResolveAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>The regime of a property already read (the property must come from the caller's org).</summary>
    Task<GuestRegistrationResolution> ResolveAsync(Property property, CancellationToken cancellationToken = default);

    /// <summary>
    /// The regimes of many properties already read, with at most two queries whatever their number (a list, a job: E31/F02/US01/T01): the answer has one entry for each property, keyed by
    /// its id, and each entry is exactly what <see cref="ResolveAsync(Property, CancellationToken)"/> says of the property alone. An empty list is an empty answer.
    /// </summary>
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

/// <summary>
/// The host's setting about notifying the guests of a Swiss property (E29/F02/US01/T01): the one writer of <c>Properties.GuestNotificationOverride</c>.
/// </summary>
public interface IGuestNotificationOverrideService
{
    /// <summary>
    /// Saves the setting of a property of the caller's org; <c>null</c> follows the regime again. Saving the value the property already has changes nothing.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><c>property_not_found</c>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 <see cref="GuestRegistrationErrorCodes.OverrideNotApplicable"/>: the property is not in Switzerland (the Italian duty is not the host's to waive).
    /// </exception>
    Task SetAsync(Guid propertyId, GuestNotificationOverride? value, string userId, CancellationToken cancellationToken = default);
}

/// <summary>The codes and message keys of the registration of the guests of a property.</summary>
public static class GuestRegistrationErrorCodes
{
    /// <summary>422: the setting is for a property in Switzerland; an Italian one follows Alloggiati Web and nothing the host says changes that.</summary>
    public const string OverrideNotApplicable = "guest_registration_override_not_applicable";

    public const string OverrideNotApplicableMessageKey = "GuestRegistrationOverrideNotApplicable";

    /// <summary>422: the catalog has no regime for the country of the property (neither Italy nor Switzerland).</summary>
    public const string CountryNotSupported = "guest_registration_country_not_supported";

    public const string CountryNotSupportedMessageKey = "GuestRegistrationCountryNotSupported";

    /// <summary>400: the body of the setting does not carry <c>override</c> (a missing member is never read as «follow the regime»; say <c>null</c>).</summary>
    public const string OverrideRequiredMessageKey = "GuestRegistrationOverrideRequired";
}
