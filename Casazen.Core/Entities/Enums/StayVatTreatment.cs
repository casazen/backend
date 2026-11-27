namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the VAT of the stay of an Italian issuer is applied (⚖ I1, a prudent default to be confirmed by an accountant): the rate of the tax rule, or the
/// exemption of art. 10 n. 8 (the nature code <c>StayExemptNature</c> of the rule, <c>N4</c>). A rule that has no exemption nature (Switzerland) ignores
/// it. Stored as a number.
/// </summary>
public enum StayVatTreatment
{
    /// <summary>The rate of the rule for the stay (10% in Italy for a para-hotel stay). The default.</summary>
    Standard = 1,

    /// <summary>Exempt under art. 10 n. 8 DPR 633/72 (nature <c>N4</c>).</summary>
    ExemptArt10 = 2,
}
