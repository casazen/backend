namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who gave notice of non-renewal of a lease (LR-14): the landlord or the tenant. Either one stops the renewal, so the lease ends with
/// its current period. Stored as the number: append only.
/// </summary>
public enum LeaseNoticeParty
{
    Landlord = 0,
    Tenant = 1,
}
