namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What the house rules say about pets (SR-07a): one of the three answers of the wizard. Stored as an integer in
/// <c>Properties.PetsPolicy</c>, <b>null = the host never said</b>: <b>append only</b>. <see cref="Yes"/> keeps the amenity
/// <see cref="Casazen.Core.Enums.PropertyAmenity.PetFriendly"/> in the list of the property (and any other answer keeps it out):
/// see <see cref="Casazen.Core.Services.PropertyHouseRules.SyncAmenities"/>.
/// </summary>
public enum PetsPolicy
{
    /// <summary>Non ammessi.</summary>
    No = 0,

    /// <summary>Su richiesta.</summary>
    OnRequest = 1,

    /// <summary>Benvenuti.</summary>
    Yes = 2,
}
