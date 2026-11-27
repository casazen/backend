namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a <c>MarketplaceActivation</c> activates, which decides what its <c>TargetCode</c> is (E09/F01/US01, NF-54). Stored as its number,
/// never reused.
/// </summary>
public enum MarketplaceActivationKind
{
    /// <summary>A sales channel of the channel manager, for one property (the target is the portal code).</summary>
    Channel = 1,

    /// <summary>A tool connected to the channel manager, for one property (the target is the portal code).</summary>
    Tool = 2,

    /// <summary>A function of the channel manager's license, for one property (the license is the accommodation's, D14; the target is the code of the license module).</summary>
    LicensedFunction = 3,

    /// <summary>A partner activated on its own site, for the whole org (the target is the code of the partner).</summary>
    Partner = 4,
}
