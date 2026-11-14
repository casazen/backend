namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the final cleaning is taxed (⚖ C2, a prudent default to be confirmed by an accountant): as an accessory of the stay (the VAT of the stay), or as
/// a service of its own (the rate of the rule for the cleaning). Stored as a number.
/// </summary>
public enum CleaningFeeTreatment
{
    /// <summary>An accessory of the stay: the same VAT as the stay (3.8% in Switzerland). The default.</summary>
    FollowsStay = 1,

    /// <summary>A service of its own: the rate of the rule for the cleaning (8.1% in Switzerland, 22% in Italy).</summary>
    Service = 2,
}
