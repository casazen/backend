namespace Casazen.Core.Entities.Enums;

/// <summary>
/// STR fiscal regime for a property+tax year. Distinct from LTR <see cref="FiscalRegime"/> on leases.
/// </summary>
public enum StrFiscalRegime
{
    CedolareSecca21 = 0,
    CedolareSecca26 = 1,
    RegimeOrdinario = 2,
    RegimeForfettario = 3
}

public enum WithholdingSource
{
    None = 0,
    AutoOta = 1,
    Manual = 2
}
