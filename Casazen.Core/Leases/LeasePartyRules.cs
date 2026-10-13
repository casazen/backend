using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Leases;

/// <summary>Stable error codes of the parties of a lease (LT-14), answered as 422 with these codes.</summary>
public static class LeasePartyErrorCodes
{
    public const string LandlordRequired = "lease_landlord_required";
    public const string TenantRequired = "lease_tenant_required";
    public const string TooMany = "lease_parties_too_many";
    public const string FiscalCodeInvalid = "lease_party_fiscal_code_invalid";
    public const string FiscalCodeDuplicate = "lease_party_fiscal_code_duplicate";
}

/// <summary>
/// Parties of a new lease (LT-14, A7-28): one or more landlords (co-owners, spouses) and one or more tenants, each with
/// a valid Italian fiscal code (<see cref="ItalianFiscalCode"/>: 16 characters with the check character, or 11 digits),
/// no fiscal code twice in the same lease. The RLI and the contract need every party.
/// </summary>
public static class LeasePartyRules
{
    /// <summary>At most this many landlords, and as many tenants, per lease (a sanity limit; frontend mirror).</summary>
    public const int MaxPerRole = 10;

    /// <param name="parties">Role and fiscal code of each party, in the order entered.</param>
    /// <exception cref="DomainRuleException">The first rule broken, with its <see cref="LeasePartyErrorCodes"/> code.</exception>
    public static void Ensure(IReadOnlyList<(PartyRole Role, string? FiscalCode)> parties)
    {
        ArgumentNullException.ThrowIfNull(parties);

        var landlords = parties.Count(p => p.Role == PartyRole.Landlord);
        var tenants = parties.Count(p => p.Role == PartyRole.Tenant);
        if (landlords == 0)
            throw new DomainRuleException(LeasePartyErrorCodes.LandlordRequired, "LeaseLandlordRequired");
        if (tenants == 0)
            throw new DomainRuleException(LeasePartyErrorCodes.TenantRequired, "LeaseTenantRequired");
        if (landlords > MaxPerRole || tenants > MaxPerRole)
            throw new DomainRuleException(LeasePartyErrorCodes.TooMany, "LeasePartiesTooMany", MaxPerRole);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, fiscalCode) in parties)
        {
            var normalized = ItalianFiscalCode.Normalize(fiscalCode);
            if (!ItalianFiscalCode.IsValid(normalized))
                throw new DomainRuleException(LeasePartyErrorCodes.FiscalCodeInvalid, "LeasePartyFiscalCodeInvalid");
            if (!seen.Add(normalized))
                throw new DomainRuleException(LeasePartyErrorCodes.FiscalCodeDuplicate, "LeasePartyFiscalCodeDuplicate");
        }
    }
}
