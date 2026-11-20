using Casazen.Core.Services;

namespace Casazen.Core.Regulatory.GuestRegistration;

/// <summary>
/// What a Swiss property still lacks before its guests can be notified (E29/F02/US01/T02, NF-129): the one definition read by the activation of the
/// property, the cockpit, the onboarding part «Guest registration» and <c>GET api/properties/{id}/guest-registration</c> (<c>identifierMissing</c>).
/// Italy has nothing to lack here: the Italian rules are untouched.
/// </summary>
/// <remarks>
/// <para><b>Two reasons, in this order.</b> <see cref="MunicipalityMissing"/>: the municipality of the property is not known (none stored, none derived
/// from its postal code and city, or several left open), so its canton, and with it the regime, is not known either. <see cref="TicinoStructureIdMissing"/>:
/// the property resolves to canton Ticino and has no valid structure number, so the file for the police portal cannot be written (the same rule as the
/// file: <see cref="TicinoStructureId.IsValid"/>). A property without a municipality is not asked for the structure number: nobody knows yet that it is in
/// Ticino.</para>
/// <para><b>The host's setting is not read.</b> Whether the host said the notification is required or not does not change what the property lacks, and nothing
/// may read that setting while <c>Features:GuestRegistrationRegimes</c> is off.</para>
/// </remarks>
public static class GuestRegistrationReadiness
{
    /// <summary>The municipality of a Swiss property is missing or ambiguous (the host chooses it in the property form).</summary>
    public const string MunicipalityMissing = "swiss_municipality_missing";

    /// <summary>The property is in Ticino and has no valid «Numero identificativo LEAR / NO-LEAR».</summary>
    public const string TicinoStructureIdMissing = "ticino_structure_id_missing";

    /// <summary>
    /// The reasons a property lacks something, empty when it lacks nothing or is not Swiss. <paramref name="place"/> is where the one place resolver puts it
    /// (<c>null</c>: none could be derived).
    /// </summary>
    public static IReadOnlyList<string> MissingOf(string? country, SwissPlace? place, string? ticinoStructureId)
    {
        if (!SwissPlace.IsSwissCountry(country))
            return [];

        if (place is not { IsKnown: true })
            return [MunicipalityMissing];

        var regime = GuestRegistrationRegimeCatalog.ForCanton(place.CantonCode);
        return string.Equals(regime.Code, GuestRegistrationRegimeCatalog.TicinoCode, StringComparison.Ordinal) && !TicinoStructureId.IsValid(ticinoStructureId)
            ? [TicinoStructureIdMissing]
            : [];
    }
}
