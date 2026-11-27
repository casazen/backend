namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kind of accommodation a short-stay property is (SR-07a, the first step of «Aggiungi immobile»): the five choices of the
/// wizard. Stored as an integer in <c>Properties.AccommodationType</c>, <b>null = the host never said</b> (every property that
/// existed before the column): <b>append only</b>, never renumber or reuse a value.
/// </summary>
public enum AccommodationType
{
    Apartment = 0,

    HouseOrVilla = 1,

    TrulloOrMasseria = 2,

    LoftOrPenthouse = 3,

    RoomOrSuite = 4,
}
