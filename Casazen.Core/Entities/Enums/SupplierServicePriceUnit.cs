namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What the price of a <see cref="Casazen.Core.Entities.SupplierServiceListing"/> is for (the unit next to "da 45 €"):
/// the four units of the supplier console.
/// </summary>
public enum SupplierServicePriceUnit
{
    /// <summary>Per job ("a intervento").</summary>
    PerJob = 0,

    /// <summary>Per hour ("all'ora").</summary>
    PerHour = 1,

    /// <summary>Per set, e.g. of linen ("a set").</summary>
    PerSet = 2,

    /// <summary>Per square meter ("al m²").</summary>
    PerSquareMeter = 3,
}
