namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What the house rules say about smoking (SR-07a): one of the three answers of the wizard. Stored as an integer in
/// <c>Properties.SmokingPolicy</c>, <b>null = the host never said</b>: <b>append only</b>. <see cref="Yes"/> keeps the amenity
/// <see cref="Casazen.Core.Enums.PropertyAmenity.SmokingAllowed"/> in the list of the property (and any other answer keeps it
/// out): see <see cref="Casazen.Core.Services.PropertyHouseRules.SyncAmenities"/>.
/// </summary>
public enum SmokingPolicy
{
    /// <summary>Vietato.</summary>
    No = 0,

    /// <summary>Solo all'aperto.</summary>
    OutdoorsOnly = 1,

    /// <summary>Consentito.</summary>
    Yes = 2,
}
