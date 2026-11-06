using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>
/// The integration settings of the activation with Octorate (NF-108, ONB-05, E10/F01/US02/T01): one overview of the connection and of what can
/// be reopened, and the reopen of any part of the plan, so the onboarding is never a one-off (change a sender, add a channel or properties,
/// reconnect after a revocation). It reads what the other epics stored and answers where to continue: it never does their work.
/// </summary>
/// <remarks>
/// <para><b>Reads only, codes and counts only.</b> The connection is read through a projection of its status and its last sync: the OAuth tokens
/// (and their ciphertext) are never selected, so they cannot reach an answer (ADR-023-R2, R13).</para>
/// <para><b>Reach.</b> The overview counts the short-rent, active properties the scope reaches (AM-03), the same set the parts of the plan count.
/// A property of another org, or one the scope does not reach, is in no count. The reopen is an org-level decision, like the deferral it clears:
/// the web layer lets only an org administrator call it.</para>
/// <para><b>Reopen never changes data.</b> It clears the deferral of the part and closes its "To do" through <see cref="IOnboardingPlanService"/>,
/// and says where to continue; the wizard that does the part changes the data. A sender change therefore goes through the sender switch of E03
/// (its confirmation and its guards): there is no second path here, so two automatic senders for one property stay impossible.</para>
/// </remarks>
public interface IOnboardingIntegrationService
{
    /// <summary>
    /// The overview of <paramref name="orgId"/> over what <paramref name="scope"/> reaches: the connection of this environment with
    /// <see cref="OnboardingConnectionHealth.ReconnectRequired"/>, the plan parts with <see cref="OnboardingIntegrationPart.Reopenable"/>, the
    /// properties for each sender of the Alloggiati and ISTAT communications, and the channel states for each publication status.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is not a scope of <paramref name="orgId"/>.</exception>
    Task<OnboardingIntegrationOverview> GetOverviewAsync(Guid orgId, HostScope scope, CancellationToken ct);

    /// <summary>
    /// Reopens a part of the plan: clears its deferral, closes its "To do" and answers the wizard to continue in. 422
    /// <see cref="OnboardingIntegrationCodes.PartNotReopenable"/> for a part nobody provides (there is no wizard to go back to).
    /// </summary>
    Task<OnboardingPartReopen> ReopenPartAsync(Guid orgId, string userId, OnboardingPartKey key, CancellationToken ct);
}

/// <summary>The stable codes of the integration settings: errors and the focus of a reopen. Never rename one.</summary>
public static class OnboardingIntegrationCodes
{
    /// <summary>422: the part is not available (nobody provides it), so there is nothing to go back to.</summary>
    public const string PartNotReopenable = "onboarding_part_not_reopenable";
}

/// <summary>Where inside the wizard of a part a reopen should continue (<see cref="OnboardingPartReopen.Focus"/>): codes the client maps.</summary>
public static class OnboardingReopenFocus
{
    /// <summary>The compliance wizard continues on the choice of the sender of the Alloggiati and ISTAT communications.</summary>
    public const string Sender = "sender";

    /// <summary>The focus of a reopen of <paramref name="key"/>; <c>null</c> when the launch target alone says where to continue.</summary>
    public static string? Of(OnboardingPartKey key) => key == OnboardingPartKey.Compliance ? Sender : null;
}

/// <summary>
/// The integration settings of an org (NF-108): the connection, the parts of the plan, and what the org has chosen so far. Codes, counts and
/// instants only: no token, no property, no person.
/// </summary>
/// <param name="Connection">The org's connection to Octorate in this environment.</param>
/// <param name="Parts">The eleven parts of the plan in order, each with whether it can be reopened.</param>
/// <param name="Senders">The properties for each sender of the Alloggiati and ISTAT communications.</param>
/// <param name="Channels">The channel states for each publication status, every status listed (zero when none).</param>
public sealed record OnboardingIntegrationOverview(
    OnboardingConnectionHealth Connection,
    IReadOnlyList<OnboardingIntegrationPart> Parts,
    OnboardingSenderCounts Senders,
    IReadOnlyList<OnboardingChannelStatusCount> Channels);

/// <summary>
/// The health of the org's connection to Octorate in this environment. Never a token or its ciphertext (ADR-023-R2, R13).
/// </summary>
/// <param name="Status">The status of the connection; <c>null</c> when the org never connected in this environment.</param>
/// <param name="Environment">The Octorate environment of this CasaZen: Test (the sandbox) or Production.</param>
/// <param name="LastSyncedAt">When CasaZen last read the org's account (UTC); <c>null</c> when it never did.</param>
/// <param name="ReconnectRequired">True when the status is Disconnected or Error: the host has to connect again (a missing connection is not a reconnect).</param>
public sealed record OnboardingConnectionHealth(
    OctorateConnectionStatus? Status,
    OctorateEnvironment Environment,
    DateTime? LastSyncedAt,
    bool ReconnectRequired);

/// <summary>A part of the plan as the settings show it.</summary>
/// <param name="Plan">The part with its state, as the plan derived it for the caller's reach.</param>
/// <param name="Reopenable">False for a part nobody provides (<see cref="OnboardingPartStates.NotAvailable"/>): there is no wizard to go back to.</param>
public sealed record OnboardingIntegrationPart(OnboardingPlanPart Plan, bool Reopenable);

/// <summary>
/// The properties the caller reaches for each sender of the Alloggiati and ISTAT communications, from the stored choices of E03. A property
/// with no stored choice is <paramref name="NotSet"/> (its default of PO-02 is not a choice); a property outside Italy has no sender to choose
/// and is in no count.
/// </summary>
/// <param name="ChannelManager">The channel manager sends.</param>
/// <param name="CasaZen">CasaZen sends.</param>
/// <param name="Manual">The host sends the file.</param>
/// <param name="NotSet">No stored choice.</param>
public sealed record OnboardingSenderCounts(int ChannelManager, int CasaZen, int Manual, int NotSet);

/// <summary>How many channel states of the properties the caller reaches are in one publication status.</summary>
/// <param name="Status">The publication status.</param>
/// <param name="Count">How many <c>ListingChannelState</c> rows are in it.</param>
public sealed record OnboardingChannelStatusCount(ChannelPublishStatus Status, int Count);

/// <summary>Where a reopen continues: the wizard of the part and, when the wizard has several places, the one to open.</summary>
/// <param name="Key">The part.</param>
/// <param name="Code">Its stable code.</param>
/// <param name="LaunchTarget">One of <see cref="OnboardingLaunchTargets"/>: the wizard that does the part.</param>
/// <param name="Focus">One of <see cref="OnboardingReopenFocus"/>; <c>null</c> when the launch target alone says where to continue.</param>
public sealed record OnboardingPartReopen(OnboardingPartKey Key, string Code, string LaunchTarget, string? Focus);
