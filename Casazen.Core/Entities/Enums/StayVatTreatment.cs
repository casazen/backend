namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the VAT of the stay of an Italian issuer is applied (⚖ I1, a prudent default to be confirmed by an accountant): the rate of the tax rule, or the
/// exemption of art. 10 n. 8 (the nature code <c>StayExemptNature</c> of the rule, <c>N4</c>). A rule that has no exemption nature (Switzerland) ignores
/// it. Stored as a number.
/// </summary>
public enum StayVatTreatment
{
    Standard = 1,

    ExemptArt10 = 2,
}
