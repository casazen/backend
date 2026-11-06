namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The work a marketplace partner does for the host, which is how the Partners group is ordered (PO-11): the members are declared in
/// that order, and their numbers keep it. Stored as its number, never reused.
/// </summary>
public enum MarketplacePartnerJob
{
    /// <summary>Selling the accommodations.</summary>
    Sell = 1,

    /// <summary>Getting into the property: locks and access.</summary>
    AccessProperty = 2,

    /// <summary>Communications and obligations the law asks for.</summary>
    Compliance = 3,

    /// <summary>Cleaning and the work around the property.</summary>
    Cleaning = 4,

    /// <summary>Setting the prices.</summary>
    Prices = 5,

    /// <summary>Accounting and the other administration.</summary>
    Administration = 6,
}
