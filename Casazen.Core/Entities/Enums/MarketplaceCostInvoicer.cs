namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who invoices the cost of a card of the marketplace (E09/F01/US01, NF-54). Stored as its number, never reused. <see cref="Unknown"/> is
/// the default: a card never says who invoices when nobody knows.
/// </summary>
public enum MarketplaceCostInvoicer
{
    /// <summary>Not known, so nothing is said about the cost.</summary>
    Unknown = 0,

    /// <summary>The partner invoices the host on its own.</summary>
    Partner = 1,

    /// <summary>CasaZen invoices the host.</summary>
    CasaZen = 2,

    /// <summary>The channel manager invoices the host (its license).</summary>
    ChannelManager = 3,
}
