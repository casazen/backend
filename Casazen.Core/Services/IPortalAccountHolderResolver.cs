using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Who holds the account of a listing on a portal (E14/F01/US01/T01, ⚖ I10, acceptance step C2 of the pilot): the override of the listing and portal
/// (<see cref="PropertyChannelAccountHolder"/>), else the default of the organization (<see cref="OrgFiscalIssuerProfile.PortalAccountHolder"/>, the owner
/// when the organization has no profile). The one place the holder is read: the owner invoice (E14), the posting of the owner ledger (who bears a commission,
/// FIX/BE-V2-NO-RECHARGE), the statement labels (E13), the VAT summary (E15) and the TD17 (E17) ask here, never the rows themselves.
/// </summary>
public interface IPortalAccountHolderResolver
{
    Task<PortalAccountHolder> ResolveAsync(Guid propertyId, BookingSource channel, CancellationToken ct = default);

    Task<PortalAccountHolders> ForOrgAsync(Guid orgId, CancellationToken ct = default);
}

public sealed record PortalAccountHolders(
    PortalAccountHolder Default,
    bool RechargePortalCommissions,
    IReadOnlyDictionary<(Guid PropertyId, BookingSource Channel), PortalAccountHolder> Overrides)
{
    public static PortalAccountHolders Defaults { get; } = new(PortalAccountHolder.Owner, true, new Dictionary<(Guid, BookingSource), PortalAccountHolder>());

    public PortalAccountHolder Resolve(Guid propertyId, BookingSource channel) =>
        Overrides.TryGetValue((propertyId, channel), out var holder) ? holder : Default;

    public bool RechargesCommissionOf(Guid propertyId, BookingSource channel) =>
        RechargePortalCommissions && Resolve(propertyId, channel) == PortalAccountHolder.Agency;

    public bool IsAgencyAccountWithoutRecharge(Guid propertyId, BookingSource channel) =>
        !RechargePortalCommissions && Resolve(propertyId, channel) == PortalAccountHolder.Agency;
}
