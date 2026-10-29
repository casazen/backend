namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kind of accommodation a short-stay property is (SR-07a, the first step of «Aggiungi immobile»): the five choices of the
/// wizard. Stored as an integer in <c>Properties.AccommodationType</c>, <b>null = the host never said</b> (every property that
/// existed before the column): <b>append only</b>, never renumber or reuse a value.
/// </summary>
public enum AccommodationType
{
    /// <summary>Appartamento.</summary>
    Apartment = 0,

    /// <summary>Casa o villa.</summary>
    HouseOrVilla = 1,

    /// <summary>Trullo o masseria.</summary>
    TrulloOrMasseria = 2,

    /// <summary>Loft o attico.</summary>
    LoftOrPenthouse = 3,

    /// <summary>Stanza o suite.</summary>
    RoomOrSuite = 4,
}
