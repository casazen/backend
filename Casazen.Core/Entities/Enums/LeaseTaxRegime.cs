namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Tax regime chosen by the landlord for a lease (LT-10, A7-13), independent of the contract type
/// (<see cref="LeaseContractType"/>).
/// </summary>
public enum LeaseTaxRegime
{
    /// <summary>Cedolare secca (D.Lgs. 23/2011 art. 3).</summary>
    CedolareSecca = 0,

    /// <summary>Ordinary IRPEF regime, with registration tax and stamp duty.</summary>
    Ordinario = 1,
}
