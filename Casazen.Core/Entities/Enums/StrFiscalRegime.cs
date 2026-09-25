namespace Casazen.Core.Entities.Enums;

/// <summary>
/// STR fiscal regime for a property+tax year. Distinct from LTR <see cref="FiscalRegime"/> on leases.
/// Persisted as an integer: append new values at the end, never reorder.
/// </summary>
/// <remarks>
/// Rules (fiscale.md § CO-18): with no more than the configured number of apartments per taxpayer the host chooses
/// cedolare secca (<see cref="CedolareSecca21"/> for the one unit designated in the tax return, <see cref="CedolareSecca26"/>
/// for the others) or <see cref="IrpefOrdinaria"/>; <see cref="RegimeOrdinario"/> and <see cref="RegimeForfettario"/> are the
/// business (impresa) regimes, with partita IVA, where no OTA withholding applies.
/// </remarks>
public enum StrFiscalRegime
{
    CedolareSecca21 = 0,
    CedolareSecca26 = 1,

    /// <summary>Impresa, regime ordinario (partita IVA).</summary>
    RegimeOrdinario = 2,

    /// <summary>Impresa, regime forfettario (partita IVA).</summary>
    RegimeForfettario = 3,

    /// <summary>
    /// Ordinary IRPEF without partita IVA, the alternative to cedolare secca (fiscale.md C11). Not an impresa regime: the OTA
    /// withholding still applies. CasaZen does not compute the tax (it depends on the taxpayer's other income).
    /// </summary>
    IrpefOrdinaria = 4,
}

public enum WithholdingSource
{
    None = 0,
    AutoOta = 1,
    Manual = 2
}
