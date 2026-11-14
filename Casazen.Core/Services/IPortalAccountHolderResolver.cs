using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Who holds the account of a listing on a portal (E14/F01/US01/T01, ⚖ I10, acceptance step C2 of the pilot): the override of the listing and portal
/// (<see cref="PropertyChannelAccountHolder"/>), else the default of the organization (<see cref="OrgFiscalIssuerProfile.PortalAccountHolder"/>, the owner
/// when the organization has no profile). The one place the holder is read: the owner invoice (E14), the statement labels (E13), the VAT summary (E15)
/// and the TD17 (E17) ask here, never the rows themselves.
/// </summary>
public interface IPortalAccountHolderResolver
{
    /// <summary>The holder of the account of <paramref name="propertyId"/> on <paramref name="channel"/> (the org is the property's).</summary>
    Task<PortalAccountHolder> ResolveAsync(Guid propertyId, BookingSource channel, CancellationToken ct = default);

    /// <summary>The holders of every listing of the organization, read once: for a reader that resolves many listings (a statement, an invoice).</summary>
    Task<PortalAccountHolders> ForOrgAsync(Guid orgId, CancellationToken ct = default);
}

/// <summary>
/// The holders of the accounts of an organization: its default, whether it recharges the commissions of the accounts it holds (⚖ I11), and the
/// overrides of its listings by portal.
/// </summary>
/// <param name="Default">The holder of a listing and portal without an override.</param>
/// <param name="RechargePortalCommissions">The commissions of the accounts the agency holds are recharged on the owner invoice.</param>
/// <param name="Overrides">The holder of a listing on a portal, where it differs from the default.</param>
public sealed record PortalAccountHolders(
    PortalAccountHolder Default,
    bool RechargePortalCommissions,
    IReadOnlyDictionary<(Guid PropertyId, BookingSource Channel), PortalAccountHolder> Overrides)
{
    /// <summary>The holders of an organization that set nothing: the owner everywhere (V1), the recharge on (its default).</summary>
    public static PortalAccountHolders Defaults { get; } = new(PortalAccountHolder.Owner, true, new Dictionary<(Guid, BookingSource), PortalAccountHolder>());

    /// <summary>The holder of the account of <paramref name="propertyId"/> on <paramref name="channel"/>: its override, else the default.</summary>
    public PortalAccountHolder Resolve(Guid propertyId, BookingSource channel) =>
        Overrides.TryGetValue((propertyId, channel), out var holder) ? holder : Default;

    /// <summary>
    /// True when the commission <paramref name="channel"/> kept on a stay of <paramref name="propertyId"/> is recharged to the owner (⚖ I11): the agency
    /// holds the account (V2) and the organization recharges.
    /// </summary>
    public bool RechargesCommissionOf(Guid propertyId, BookingSource channel) =>
        RechargePortalCommissions && Resolve(propertyId, channel) == PortalAccountHolder.Agency;
}
