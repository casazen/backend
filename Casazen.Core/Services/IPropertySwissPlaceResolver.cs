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
    /// <summary>The place of <paramref name="property"/>; <c>null</c> when it is not in Switzerland or nothing can be derived.</summary>
    Task<SwissPlace?> ResolveAsync(Property property, CancellationToken cancellationToken = default);

    /// <summary>
    /// The places of many properties with at most two queries whatever their number (a list, a report, a job): the answer has an entry for each
    /// property that has a place (ambiguous ones included), keyed by <see cref="SwissPlaceLookup.PropertyId"/>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, SwissPlace>> ResolveManyAsync(IReadOnlyCollection<SwissPlaceLookup> properties, CancellationToken cancellationToken = default);

    /// <summary>
    /// The municipality the host chose for a property (the BFS number sent on create or update), as a stored place.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 <c>swiss_places_unavailable</c> when the directory is not loaded, <c>swiss_municipality_unknown</c> when it has no such number.
    /// </exception>
    Task<SwissPlace> RequireMunicipalityAsync(string municipalityCode, CancellationToken cancellationToken = default);
}

/// <summary>Where the municipality of a <see cref="SwissPlace"/> comes from.</summary>
public enum SwissPlaceSource
{
    /// <summary>The municipality stored on the property (the host confirmed it).</summary>
    Stored,

    /// <summary>Derived from the postal code and the city of the property: not stored, to be confirmed by the host.</summary>
    Derived,
}

/// <summary>
/// Where a Swiss property is: the BFS number and official name of its municipality and the two letters of its canton, where they come from, and
/// whether its postal code and city leave several municipalities open (<see cref="Ambiguous"/>: then no municipality and no canton at all).
/// </summary>
public sealed record SwissPlace(string? MunicipalityCode, string? MunicipalityName, string? CantonCode, SwissPlaceSource Source, bool Ambiguous)
{
    /// <summary>The canton of Ticino (ISO 3166-2:CH).</summary>
    public const string TicinoCantonCode = "TI";

    /// <summary>The municipality and the canton are known (not ambiguous).</summary>
    public bool IsKnown => !Ambiguous && MunicipalityCode is not null && CantonCode is not null;

    /// <summary>The place is known and in canton <paramref name="cantonCode"/>.</summary>
    public bool IsInCanton(string cantonCode) => IsKnown && string.Equals(CantonCode, cantonCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>The place is known and in canton Ticino.</summary>
    public bool IsInTicino => IsInCanton(TicinoCantonCode);

    /// <summary>The postal code and the city of the property leave several municipalities open: nothing is chosen.</summary>
    public static SwissPlace AmbiguousPlace { get; } = new(null, null, null, SwissPlaceSource.Derived, Ambiguous: true);

    /// <summary>The country of a property is Switzerland (<c>CH</c>, case and spaces ignored).</summary>
    public static bool IsSwissCountry(string? country) => string.Equals(country?.Trim(), "CH", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the resolver reads of a property: its country, its stored municipality, its postal code and its city.</summary>
public sealed record SwissPlaceLookup(Guid PropertyId, string? Country, string? SwissMunicipalityCode, string? PostalCode, string? City)
{
    public static SwissPlaceLookup From(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new SwissPlaceLookup(property.Id, property.Country, property.SwissMunicipalityCode, property.PostalCode, property.City);
    }
}

/// <summary>The codes and message keys of the Swiss municipality of a property.</summary>
public static class SwissPlaceErrorCodes
{
    /// <summary>422: a Swiss municipality sent for a property that is not in Switzerland.</summary>
    public const string NotApplicable = "swiss_municipality_not_applicable";

    public const string NotApplicableMessageKey = "SwissMunicipalityNotApplicable";

    /// <summary>422: the BFS number sent is not a municipality of the directory.</summary>
    public const string Unknown = "swiss_municipality_unknown";

    public const string UnknownMessageKey = "SwissMunicipalityUnknown";

    /// <summary>422: the directory of the Swiss municipalities is not loaded, so a municipality cannot be checked.</summary>
    public const string Unavailable = "swiss_places_unavailable";

    public const string UnavailableMessageKey = "SwissPlacesUnavailable";

    /// <summary>The message key of a <c>swissMunicipalityCode</c> that is not a BFS number (400, field error).</summary>
    public const string InvalidFormatMessageKey = "SwissMunicipalityCodeInvalid";

    /// <summary>A BFS number as the requests accept it: one to four digits (a leading zero is dropped when it is stored).</summary>
    public const string CodePattern = "^[0-9]{1,4}$";
}
