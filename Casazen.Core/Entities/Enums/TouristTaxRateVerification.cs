namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the amount of a <see cref="TouristTaxRate"/> was checked against its source. Same legend as the
/// <c>verified</c> column of <c>Casazen.Infrastructure/Data/Seeds/tourist-tax/rates.csv</c> (task RS-7,
/// <c>.claude/context/regulations/imposta_soggiorno.md</c>, "Tariffe verificate (2026-09)").
/// </summary>
public enum TouristTaxRateVerification
{
    /// <summary>U: read in a page or an act of the comune (or of another public body).</summary>
    Official = 0,

    /// <summary>D: deduced by us from official or third-party data (for example from a percentage or a start-date rule).</summary>
    Deduced = 1,

    /// <summary>T: third-party source (software vendors, blogs, press). A hint, not a source.</summary>
    ThirdParty = 2,
}
