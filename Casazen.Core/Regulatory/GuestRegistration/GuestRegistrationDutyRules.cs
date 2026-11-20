using Casazen.Core.Enums;

namespace Casazen.Core.Regulatory.GuestRegistration;

/// <summary>
/// The one rule that says what CasaZen treats as the duty of a property (E29/F02/US01/T01, NF-125, decisions D5 and D12): raised, not raised, or left
/// to the host. It is made of the regime the property resolves to and of the host's own setting, and of nothing else (no booking, no guest).
/// </summary>
/// <remarks>
/// <para><b>The table.</b> The host's setting wins: <see cref="GuestNotificationOverride.Required"/> is <see cref="GuestRegistrationEffectiveDuty.Required"/>,
/// <see cref="GuestNotificationOverride.NotRequired"/> is <see cref="GuestRegistrationEffectiveDuty.NotRequired"/>. With none the regime decides:
/// Ticino is <c>Required</c>; a regime that is <b>verified</b> as an obligation to report to an authority that covers private rentals (Italy, and the cantons
/// that read so, Schaffhausen and Zug) is <c>Required</c>; every other regime is <c>Ask</c>.</para>
/// <para><b>Never a false alarm.</b> A regime whose sources are partial or not verified, whose obligation is only to keep a register or is a faculty, or that
/// does not say it covers private rentals, is <c>Ask</c>: the host decides and nothing is raised in red meanwhile. The rule asks for the certainty as well as
/// the two facts, so an entry that a later reading downgrades stops raising the duty by itself.</para>
/// </remarks>
public static class GuestRegistrationDutyRules
{
    /// <summary>What the regime says when the host has not decided.</summary>
    public static GuestRegistrationEffectiveDuty DefaultDuty(GuestRegistrationRegime regime)
    {
        ArgumentNullException.ThrowIfNull(regime);

        if (string.Equals(regime.Code, GuestRegistrationRegimeCatalog.TicinoCode, StringComparison.Ordinal))
            return GuestRegistrationEffectiveDuty.Required;

        var reportsAndCoversPrivateRentals = regime is
        {
            Obligation: GuestRegistrationObligation.ReportToAuthority,
            PrivateRentals: GuestRegistrationPrivateRentals.Yes,
            Certainty: GuestRegistrationCertainty.Verified,
        };
        return reportsAndCoversPrivateRentals ? GuestRegistrationEffectiveDuty.Required : GuestRegistrationEffectiveDuty.Ask;
    }

    /// <summary>The duty in effect: the host's setting when there is one, else <see cref="DefaultDuty"/>.</summary>
    public static GuestRegistrationEffectiveDuty EffectiveDuty(GuestRegistrationRegime regime, GuestNotificationOverride? hostChoice) => hostChoice switch
    {
        GuestNotificationOverride.Required => GuestRegistrationEffectiveDuty.Required,
        GuestNotificationOverride.NotRequired => GuestRegistrationEffectiveDuty.NotRequired,
        _ => DefaultDuty(regime),
    };
}
