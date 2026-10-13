namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Legacy combined value of a lease: it mixed the contract type with the tax regime (A7-13), so "canone concordato under
/// the ordinary regime" had no value. Since LT-10 a lease stores <see cref="LeaseContractType"/> and
/// <see cref="LeaseTaxRegime"/>; this value is derived from them (<see cref="Casazen.Core.Leases.LeaseContractTerms.LegacyFiscalRegime"/>)
/// and still selects the contract template, the IMU notice and the cedolare advisory. Accepted as input only from
/// clients that do not send the contract type yet.
/// </summary>
public enum FiscalRegime
{
    /// <summary>Canone libero with the cedolare secca.</summary>
    CedolareSecca,

    /// <summary>Canone libero with the ordinary regime.</summary>
    RegimeOrdinario,

    /// <summary>Canone concordato, whatever the tax regime.</summary>
    CanoneConcordato
}
