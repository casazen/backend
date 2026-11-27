using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The one place that says where a Swiss property is (E29/F01/US01/T02, NF-124, decisions D3 and D4): its municipality and canton, read by every
/// rule that depends on them (the Ticino police notification, the OTR report, the year-end count of Ticino, the policy of the identity documents)
/// instead of a copied «country CH and canton TI» test. The Italian analogue is <see cref="IPropertyComuneResolver"/>.
/// </summary>
/// <remarks>
/// <para><b>Rules.</b> A property outside Switzerland has no place (<c>null</c>). A stored <see cref="Property.SwissMunicipalityCode"/> that the
/// directory has wins (<see cref="SwissPlaceSource.Stored"/>). Otherwise the place is derived from the postal code and the city
/// (<see cref="SwissPlaceSource.Derived"/>): the four digits of the postal code (<see cref="SwissPlaceCodes.FourDigitPostalCode"/>); when the
/// localities of that postal code all lie in one municipality, that one; when several, the one whose official name equals the city (only when they
/// all lie in one canton: a postal code in two cantons, like 6533, is never decided on the name of a municipality), else the one that has a locality
/// of that postal code whose name equals the city (names compared by <see cref="SwissLocality.NormalizeName"/>); still several
/// (or none of them named) is <see cref="SwissPlace.Ambiguous"/> with no municipality and no canton: the resolver never chooses silently, the
/// property form asks the host. A malformed postal code, or one the directory does not have, gives no place.</para>
/// <para><b>Read only.</b> Resolving never writes: a derived place is not stored behind the host's back. The property save path stores a
/// municipality only when the caller sent it (the host confirmed it in the form).</para>
/// </remarks>
public interface IPropertySwissPlaceResolver
{
    Task<SwissPlace?> ResolveAsync(Property property, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, SwissPlace>> ResolveManyAsync(IReadOnlyCollection<SwissPlaceLookup> properties, CancellationToken cancellationToken = default);

    Task<SwissPlace> RequireMunicipalityAsync(string municipalityCode, CancellationToken cancellationToken = default);
}

public enum SwissPlaceSource
{
    Stored,

    Derived,
}

public sealed record SwissPlace(string? MunicipalityCode, string? MunicipalityName, string? CantonCode, SwissPlaceSource Source, bool Ambiguous)
{
    public const string TicinoCantonCode = "TI";

    public bool IsKnown => !Ambiguous && MunicipalityCode is not null && CantonCode is not null;

    public bool IsInCanton(string cantonCode) => IsKnown && string.Equals(CantonCode, cantonCode, StringComparison.OrdinalIgnoreCase);

    public bool IsInTicino => IsInCanton(TicinoCantonCode);

    public static SwissPlace AmbiguousPlace { get; } = new(null, null, null, SwissPlaceSource.Derived, Ambiguous: true);

    public const string CountryCode = "CH";

    public static bool IsSwissCountry(string? country) => string.Equals(country?.Trim(), CountryCode, StringComparison.OrdinalIgnoreCase);
}

public sealed record SwissPlaceLookup(Guid PropertyId, string? Country, string? SwissMunicipalityCode, string? PostalCode, string? City)
{
    public static SwissPlaceLookup From(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new SwissPlaceLookup(property.Id, property.Country, property.SwissMunicipalityCode, property.PostalCode, property.City);
    }
}

public static class SwissPlaceErrorCodes
{
    public const string NotApplicable = "swiss_municipality_not_applicable";

    public const string NotApplicableMessageKey = "SwissMunicipalityNotApplicable";

    public const string Unknown = "swiss_municipality_unknown";

    public const string UnknownMessageKey = "SwissMunicipalityUnknown";

    public const string Unavailable = "swiss_places_unavailable";

    public const string UnavailableMessageKey = "SwissPlacesUnavailable";

    public const string InvalidFormatMessageKey = "SwissMunicipalityCodeInvalid";

    public const string CodePattern = "^[0-9]{1,4}$";
}
