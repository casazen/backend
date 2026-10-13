using Casazen.Core.Entities.Enums;
using Microsoft.Extensions.Configuration;

namespace Casazen.Core.Services;

/// <summary>
/// "Accesso aperto" (BL-01, decisions D-A and D33): every plan is paid, but for now the platform serves every org as if it
/// had at least one tier, without touching prices, tiers, subscriptions or payments. Two settings, read at every call (a
/// Railway variable change redeploys the service, so the new value applies at once):
/// <list type="bullet">
/// <item><c>Entitlement:OpenAccess:Enabled</c> (<see cref="EnabledVariable"/>): <c>true</c> or <c>false</c>. Missing or
/// empty = <b>off</b>, the default: nothing changes.</item>
/// <item><c>Entitlement:OpenAccess:Tier</c> (<see cref="TierVariable"/>): the name of a <see cref="PlanTier"/>, case
/// insensitive. Missing or empty = <see cref="DefaultTier"/> (<c>Scale</c>).</item>
/// </list>
/// With it on, the <b>effective</b> tier of every org (<see cref="IEntitlementService.ResolveEffectiveTier"/>) is at least the
/// configured one, and never lower than the one its subscription pays for (<see cref="OpenAccessSetting.Apply"/>). It is an
/// override on read: the stored tier (<c>Org.PlanTier</c>), the Stripe data, the plan change rules
/// (<see cref="PlanChangePolicy"/>) and the billing pages are not touched. A value that is not valid stops the startup
/// (<see cref="GetErrors"/>); a host that skipped that check reads it as <b>off</b> (<see cref="Read"/>), so a typo never opens
/// the access. Runbook: <see cref="Runbook"/>.
/// </summary>
public static class OpenAccess
{
    /// <summary>Configuration key of the switch.</summary>
    public const string EnabledSetting = "Entitlement:OpenAccess:Enabled";

    /// <summary>Configuration key of the tier every org gets at least.</summary>
    public const string TierSetting = "Entitlement:OpenAccess:Tier";

    /// <summary>Environment variable of <see cref="EnabledSetting"/> (Railway).</summary>
    public const string EnabledVariable = "Entitlement__OpenAccess__Enabled";

    /// <summary>Environment variable of <see cref="TierSetting"/> (Railway).</summary>
    public const string TierVariable = "Entitlement__OpenAccess__Tier";

    public const string Runbook = "docs/runbooks/open-access.md";

    /// <summary>The tier used when the switch is on and no tier is set: the top plan (decision D33).</summary>
    public const PlanTier DefaultTier = PlanTier.Scale;

    /// <summary>
    /// The setting in force. <see cref="OpenAccessSetting.Off"/> when the switch is off or missing, and also when any value
    /// is not valid (fail closed: the startup validation already refused it, this is the net for a host that skipped it).
    /// </summary>
    public static OpenAccessSetting Read(IConfiguration configuration) => Parse(configuration, out _);

    /// <summary>
    /// Configuration errors naming the variables and never their values, empty when the settings are valid (also when they
    /// are missing: the defaults are valid). Run at startup by <c>OpenAccessConfiguration</c> (Web).
    /// </summary>
    public static IReadOnlyList<string> GetErrors(IConfiguration configuration)
    {
        Parse(configuration, out var errors);
        return errors;
    }

    private static OpenAccessSetting Parse(IConfiguration configuration, out List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        errors = [];

        var enabled = false;
        var enabledValue = configuration[EnabledSetting];
        if (!string.IsNullOrWhiteSpace(enabledValue) && !bool.TryParse(enabledValue, out enabled))
        {
            enabled = false;
            errors.Add($"{EnabledVariable} must be true or false (or empty, which means false). See {Runbook}.");
        }

        var tier = DefaultTier;
        var tierValue = configuration[TierSetting];
        if (!string.IsNullOrWhiteSpace(tierValue) && !PlanCatalog.TryParseTier(tierValue, out tier))
        {
            tier = DefaultTier;
            errors.Add(
                $"{TierVariable} must be the name of a plan ({string.Join(", ", PlanCatalog.All.Select(entry => entry.Tier))}) " +
                $"or empty, which means {DefaultTier}. See {Runbook}.");
        }

        return errors.Count == 0 ? new OpenAccessSetting(enabled, tier) : OpenAccessSetting.Off;
    }
}

/// <summary>The open access of this deployment: whether it is on, and the tier every org gets at least (<see cref="OpenAccess"/>).</summary>
/// <param name="Enabled"><c>true</c> when <c>Entitlement:OpenAccess:Enabled</c> is on.</param>
/// <param name="Tier">The tier every org gets at least while <paramref name="Enabled"/>; meaningless when it is off.</param>
public readonly record struct OpenAccessSetting(bool Enabled, PlanTier Tier)
{
    /// <summary>No open access: every org keeps the tier its subscription pays for.</summary>
    public static OpenAccessSetting Off => new(false, OpenAccess.DefaultTier);

    /// <summary>
    /// The tier an org is served at: <paramref name="paidTier"/> (what its subscription pays for, see
    /// <see cref="IEntitlementService.ResolvePaidTier"/>) or <see cref="Tier"/> when open access is on and that is higher.
    /// Never lower than <paramref name="paidTier"/>.
    /// </summary>
    public PlanTier Apply(PlanTier paidTier) =>
        Enabled && PlanCatalog.Rank(Tier) > PlanCatalog.Rank(paidTier) ? Tier : paidTier;
}
