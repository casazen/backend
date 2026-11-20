using Casazen.Core.Entities;

namespace Casazen.Core.Regulatory;

/// <summary>
/// What the check-in asks of the guests of a property in canton Ticino on top of the Alloggiati record (NF-132, casazen/backend#979), as names of
/// <c>requiredGuestFields</c>: the properties of the guest form (<c>StayGuestSubmitDto</c>), the same convention as the statistical fields of a region
/// (<see cref="IstatGuestFieldLists"/>). The page asks exactly these; the server decides what blocks.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="DocumentType"/>, <see cref="DocumentNumber"/>: the document of every guest the police file notifies on their own row
/// (<see cref="TicinoPoliceFile.NotifiedPositions"/>: every adult, family and group members included, or every minor of a stay without adults; in a
/// group of six or more only its leader), not only of the head. Required: the check-in and the wizard do not complete without them.</item>
/// <item><see cref="ResidenceStreet"/>, <see cref="ResidencePostalCode"/>, <see cref="ResidenceCityName"/>, <see cref="ResidenceCountryName"/> (the
/// place of residence as the form already asks it: in Italy the comune, abroad the state): «Indirizzo», «CAP», «Domicilio», «Nazione» of the file.
/// Asked but optional: a guest without them is still a valid row of the file.</item>
/// <item>The plate of the car is asked once for the stay (<c>askVehiclePlate</c>), never per guest, and is optional.</item>
/// </list>
/// </remarks>
public static class TicinoCheckInFields
{
    public const string DocumentType = nameof(StayGuest.DocumentType);
    public const string DocumentNumber = nameof(StayGuest.DocumentNumber);
    public const string ResidenceStreet = nameof(StayGuest.ResidenceStreet);
    public const string ResidencePostalCode = nameof(StayGuest.ResidencePostalCode);
    public const string ResidenceCityName = nameof(StayGuest.ResidenceCityName);
    public const string ResidenceCountryName = nameof(StayGuest.ResidenceCountryName);

    /// <summary>The fields a Ticino property asks, in the order of the form.</summary>
    public static IReadOnlyList<string> Required { get; } =
        [DocumentType, DocumentNumber, ResidenceStreet, ResidencePostalCode, ResidenceCityName, ResidenceCountryName];

    /// <summary>The fields a property asks of its guests: the statistical ones its region requires, then the Ticino ones when it is in Ticino.</summary>
    public static IReadOnlyList<string> AskedOf(IReadOnlyList<string> statisticalFields, bool inTicino)
    {
        ArgumentNullException.ThrowIfNull(statisticalFields);
        return inTicino ? [.. statisticalFields, .. Required] : statisticalFields;
    }
}
