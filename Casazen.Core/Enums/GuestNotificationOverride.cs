namespace Casazen.Core.Enums;

/// <summary>
/// What the host decided about notifying the guests of a Swiss property to the authority of its canton (E29/F02/US01/T01, NF-125, decisions D5 and D12):
/// stored as its number in <c>Properties.GuestNotificationOverride</c>; <c>null</c> in the column means «follow the regime» (no override). Serialized by
/// name and numbered from 1, so that an unset value (0) is never read as a decision.
/// </summary>
/// <remarks>
/// The override is the host's own answer to a question the catalog cannot settle (a canton whose sources do not say that the rule applies to private
/// rentals) or its own exemption (a structure the law leaves out). It never decides by itself what the law asks: it only tells CasaZen whether to
/// raise the duty (<see cref="Required"/>) or to stay silent (<see cref="NotRequired"/>). Nothing reads it while <c>Features:GuestRegistrationRegimes</c>
/// is off.
/// </remarks>
public enum GuestNotificationOverride
{
    /// <summary>The host says the guests of this property must be notified: the duty is raised whatever the regime says.</summary>
    Required = 1,

    /// <summary>The host says the guests of this property need not be notified: nothing is raised, whatever the regime says.</summary>
    NotRequired = 2,
}
