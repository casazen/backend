using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The bulk configuration of an agency with many properties (NF-105, ONB-02, E10/F02/US01/T01): a choice made once and applied to all the
/// properties, to a list of them or to a filtered selection; how many are ready; and the progressive activation, a pilot group of 5 to 10
/// properties first and then the others (H10).
/// </summary>
/// <remarks>
/// <para><b>Which properties.</b> Only the active short-rent properties of the org of the scope that the scope reaches: the ones the flows of
/// the activation apply to (<see cref="IOnboardingFlowStatusService"/>). The org is bound in SQL first and then what the caller reaches
/// (<c>InScope</c>, AM-03); a property of another org, a deleted one, one outside the reach and one that is long-term or switched off are the
/// same answer as an id that does not exist, and nothing is ever selected, previewed or changed for them.</para>
/// <para><b>Never «all» by accident.</b> An empty list of ids, a filter with no criterion, a list in a filter that is empty and an empty list of
/// flows are 422: «all» is the kind <see cref="BulkSelectionKinds.All"/> and nothing else.</para>
/// <para><b>One property at a time.</b> A choice goes through the service that owns it, property by property, so every rule of that service still
/// applies (the sender through <see cref="IPropertySenderService"/>, the activation through <see cref="IOnboardingFlowActivationService"/>). This
/// service writes only the pilot group.</para>
/// </remarks>
public interface IOnboardingBulkService
{
    /// <summary>
    /// How many properties the selection holds and a sample of them (<see cref="OnboardingBulkLimits.SampleSize"/>, by name). Reads only. A selection
    /// larger than <c>Onboarding:MaxPropertiesPerCall</c> is answered with its true total and <see cref="BulkPreview.ExceedsLimit"/> set, so the
    /// client can ask for a narrower one; <see cref="ApplyAsync"/> refuses it.
    /// </summary>
    Task<BulkPreview> PreviewAsync(HostScope scope, BulkSelection selection, CancellationToken ct);

    /// <summary>
    /// Applies <paramref name="choice"/> to the selection, one property at a time through the sender switch of E03 (<c>SwitchWithoutViewAsync</c>: the switch of <c>SwitchAsync</c> without the view, so with no read of Octorate), never by
    /// writing the sender directly: the confirmation, the other sender going off, the delegation and the audit line of every change apply to each
    /// property. With <paramref name="dryRun"/> or without <paramref name="confirmed"/> nothing is written: each property is judged by the same
    /// rules (<c>CheckSwitchAsync</c>) and reported <see cref="BulkApplyOutcomes.WouldChange"/>, <see cref="BulkApplyOutcomes.Unchanged"/> or
    /// <see cref="BulkApplyOutcomes.Refused"/> with the rule. With <paramref name="confirmed"/> the result is
    /// <see cref="BulkApplyOutcomes.Applied"/>, <see cref="BulkApplyOutcomes.Unchanged"/> or <see cref="BulkApplyOutcomes.Refused"/>.
    /// The bulk never records the consent to the delegation of the transmission: a property whose org has not accepted it is refused
    /// (<c>alloggiati_delegation_required</c>), and the consent is given once, with the text read, through the sender of any property.
    /// </summary>
    /// <remarks>Errors: 422 <see cref="OnboardingBulkCodes.SelectionInvalid"/>, <see cref="OnboardingBulkCodes.SelectionTooLarge"/>, <see cref="OnboardingBulkCodes.ChoiceInvalid"/>.</remarks>
    Task<BulkApplyResult> ApplyAsync(
        HostScope scope,
        string actorUserId,
        BulkSelection selection,
        BulkChoice choice,
        bool dryRun,
        bool confirmed,
        CancellationToken ct);

    /// <summary>
    /// «142 of 250 ready»: how many properties are ready out of all of them, and for each flow how many are active, ready to be activated, still to
    /// complete and not available, with the state of the pilot group. A property is ready when every flow available for it is active or has every
    /// prerequisite met (the flow status of E10/F01/US01/T02).
    /// </summary>
    Task<BulkProgress> GetProgressAsync(HostScope scope, CancellationToken ct);

    /// <summary>The pilot group the scope reaches, with how far each flow has got on it.</summary>
    Task<BulkPilot> GetPilotAsync(HostScope scope, CancellationToken ct);

    /// <summary>
    /// Replaces the pilot group with <paramref name="propertyIds"/>: 1 to <c>Onboarding:PilotGroupMaxSize</c> (default 10) distinct properties, all of
    /// them properties of the scope that the flows apply to. Fewer than <see cref="OnboardingBulkLimits.PilotRecommendedMin"/> is accepted and
    /// reported (<see cref="BulkPilot.BelowRecommended"/>). Writes the <c>OnboardingPilotMember</c> rows in one save; for a caller who reaches only
    /// some of the properties it replaces the members it reaches and leaves the others as they are.
    /// </summary>
    /// <remarks>
    /// Errors: 422 <see cref="OnboardingActivationCodes.PropertyIdsEmpty"/> (never «none»), 422 <see cref="OnboardingBulkCodes.PilotGroupTooLarge"/>,
    /// 404 <see cref="OnboardingActivationCodes.PropertyNotFound"/> (an id of another org, deleted, outside the reach or not a property the flows
    /// apply to: nothing is written).
    /// </remarks>
    Task<BulkPilot> SetPilotAsync(HostScope scope, string actorUserId, IReadOnlyCollection<Guid> propertyIds, CancellationToken ct);

    /// <summary>
    /// Activates <paramref name="flows"/> on a cohort, through the activation service of T04 (which verifies the prerequisites of each property again
    /// and writes the row and its outbox event): <see cref="BulkCohorts.Pilot"/> the pilot group, <see cref="BulkCohorts.Rest"/> every other property.
    /// A property whose prerequisites are not met is reported <c>prerequisitesNotMet</c> with the keys that do not hold, and nothing is written for it.
    /// When the scope has more properties than the pilot maximum, <see cref="BulkCohorts.Rest"/> is refused with 409
    /// <see cref="OnboardingBulkCodes.PilotGroupNotActive"/> unless the pilot group is set and is active in every flow asked (a flow is active on
    /// the pilot when every member it is available for has it active, and at least one member has it available).
    /// </summary>
    /// <remarks>
    /// Errors: 422 <see cref="OnboardingBulkCodes.CohortInvalid"/>, <see cref="OnboardingBulkCodes.FlowsEmpty"/>,
    /// <see cref="OnboardingBulkCodes.PilotGroupEmpty"/> (the pilot cohort of a pilot group that is not set), 409 <see cref="OnboardingBulkCodes.PilotGroupNotActive"/>.
    /// </remarks>
    Task<BulkActivationResult> ActivateAsync(
        HostScope scope,
        string actorUserId,
        string cohort,
        IReadOnlyCollection<OnboardingFlow> flows,
        CancellationToken ct);
}

/// <summary>The kinds of selection (cross-repo contract): never rename one.</summary>
public static class BulkSelectionKinds
{
    /// <summary>Every property of the scope the flows apply to.</summary>
    public const string All = "all";

    /// <summary>The properties named, 1 to <c>Onboarding:MaxPropertiesPerCall</c> distinct ids.</summary>
    public const string Ids = "ids";

    /// <summary>The properties that match a filter, by comune, region, channel and the state of a flow.</summary>
    public const string Filter = "filter";

    /// <summary>The kind named by <paramref name="value"/> (case-insensitive, once trimmed), as the stable code; <c>null</c> when it is none.</summary>
    public static string? Parse(string? value)
    {
        var trimmed = value?.Trim();
        foreach (var known in new[] { All, Ids, Filter })
        {
            if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
                return known;
        }

        return null;
    }
}

/// <summary>The cohorts of the progressive activation (cross-repo contract): never rename one.</summary>
public static class BulkCohorts
{
    /// <summary>The pilot group: the first properties the flows are activated on.</summary>
    public const string Pilot = "pilot";

    /// <summary>Every property that is not in the pilot group.</summary>
    public const string Rest = "rest";

    /// <summary>The cohort named by <paramref name="value"/> (case-insensitive, once trimmed), as the stable code; <c>null</c> when it is none.</summary>
    public static string? Parse(string? value)
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, Pilot, StringComparison.OrdinalIgnoreCase))
            return Pilot;

        return string.Equals(trimmed, Rest, StringComparison.OrdinalIgnoreCase) ? Rest : null;
    }
}

/// <summary>Stable per-property outcomes of an apply (cross-repo contract): never rename one.</summary>
public static class BulkApplyOutcomes
{
    /// <summary>The choice was written (the sender changed).</summary>
    public const string Applied = "applied";

    /// <summary>The property already has what was asked: nothing changes.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>A rule of the owning service refused it, or the property is not one the caller reaches: the reason is its code. Nothing was written.</summary>
    public const string Refused = "refused";

    /// <summary>Only in a dry run: the choice would change the property. Nothing was written.</summary>
    public const string WouldChange = "wouldChange";
}

/// <summary>Stable codes of the bulk configuration (cross-repo contract): never rename one.</summary>
public static class OnboardingBulkCodes
{
    /// <summary>422: the selection is not valid (an unknown kind, a filter with no criterion or with an empty list, a code that is not one).</summary>
    public const string SelectionInvalid = "onboarding_selection_invalid";

    /// <summary>422: the selection holds more properties than <c>Onboarding:MaxPropertiesPerCall</c>.</summary>
    public const string SelectionTooLarge = "onboarding_selection_too_large";

    /// <summary>422: the choice to apply is missing or is not one this version knows.</summary>
    public const string ChoiceInvalid = "onboarding_choice_invalid";

    /// <summary>422: the pilot group has more properties than <c>Onboarding:PilotGroupMaxSize</c>.</summary>
    public const string PilotGroupTooLarge = "pilot_group_too_large";

    /// <summary>409: the other properties wait until the pilot group is set and active in every flow asked.</summary>
    public const string PilotGroupNotActive = "pilot_group_not_active";

    /// <summary>422: the pilot cohort was asked and the pilot group is not set (it never means «all»).</summary>
    public const string PilotGroupEmpty = "onboarding_pilot_group_empty";

    /// <summary>422: the cohort is neither <c>pilot</c> nor <c>rest</c>.</summary>
    public const string CohortInvalid = "onboarding_cohort_invalid";

    /// <summary>422: no flow was asked (it never means «all»).</summary>
    public const string FlowsEmpty = "onboarding_flows_empty";

    /// <summary>The reason of a <see cref="BulkApplyOutcomes.Refused"/> property that is not one the caller reaches (the same code as the sender's).</summary>
    public const string PropertyNotFound = "property_not_found";
}

/// <summary>The numbers of the bulk configuration.</summary>
public static class OnboardingBulkLimits
{
    /// <summary>How many properties the preview shows.</summary>
    public const int SampleSize = 20;

    /// <summary>The pilot group the product asks for is of at least this many properties (H10); fewer is accepted and reported.</summary>
    public const int PilotRecommendedMin = 5;

    /// <summary>The default of <c>Onboarding:PilotGroupMaxSize</c>.</summary>
    public const int DefaultPilotGroupMaxSize = 10;
}

/// <summary>
/// Which properties a bulk call is about. <see cref="Kind"/> is one of <see cref="BulkSelectionKinds"/>; <see cref="PropertyIds"/> goes with
/// <see cref="BulkSelectionKinds.Ids"/> and <see cref="Filter"/> with <see cref="BulkSelectionKinds.Filter"/>.
/// </summary>
public sealed record BulkSelection(string Kind, IReadOnlyList<Guid>? PropertyIds = null, BulkFilter? Filter = null);

/// <summary>
/// The criteria of a filtered selection: a property must meet every criterion that is given, and any value of a list. A list that is given is
/// never empty, and at least one criterion is given.
/// </summary>
/// <param name="ComuneIstatCodes">ISTAT codes (6 digits) of the comune of the property (<c>Property.ComuneIstatCode</c>).</param>
/// <param name="RegionCodes">CasaZen region codes (<c>LOM</c>) of the property (<c>Property.RegionCode</c>).</param>
/// <param name="ChannelCodes">Codes of a channel (<c>booking_xml</c>) the property has a channel state for, whatever its status.</param>
/// <param name="MissingFlow">The flow is available for the property and not active yet (state <c>toComplete</c>).</param>
/// <param name="FlowState">The flow is in exactly this state on the property.</param>
public sealed record BulkFilter(
    IReadOnlyList<string>? ComuneIstatCodes = null,
    IReadOnlyList<string>? RegionCodes = null,
    IReadOnlyList<string>? ChannelCodes = null,
    OnboardingFlow? MissingFlow = null,
    BulkFlowStateFilter? FlowState = null);

/// <summary>A flow and one of its states (<c>active</c>, <c>toComplete</c>, <c>notAvailable</c>) on a property.</summary>
public sealed record BulkFlowStateFilter(OnboardingFlow Flow, string State);

/// <summary>The choice to apply. The one choice of this version is the sender of the Alloggiati Web and ISTAT communications.</summary>
/// <param name="ComplianceSender">The sender: <c>ChannelManager</c>, <c>CasaZen</c> or <c>Manual</c>; the switch of E03 decides which one a property may have.</param>
public sealed record BulkChoice(ComplianceSenderType? ComplianceSender);

/// <summary>One property of the sample of a preview.</summary>
public sealed record BulkPropertySample(Guid PropertyId, string Name, string City);

/// <summary>What a selection holds.</summary>
/// <param name="Total">The properties of the selection.</param>
/// <param name="Sample">The first of them, by name.</param>
/// <param name="NotFound">The ids asked for that are not properties of the selection (another org, deleted, outside the reach, not short-rent or not active); 0 for the other kinds.</param>
/// <param name="ExceedsLimit">True when <paramref name="Total"/> is more than <paramref name="MaxPropertiesPerCall"/>: it cannot be applied as it is.</param>
/// <param name="MaxPropertiesPerCall"><c>Onboarding:MaxPropertiesPerCall</c>.</param>
public sealed record BulkPreview(
    int Total,
    IReadOnlyList<BulkPropertySample> Sample,
    int NotFound,
    bool ExceedsLimit,
    int MaxPropertiesPerCall);

/// <summary>The outcome for one property. <see cref="Name"/> and <see cref="City"/> are <c>null</c> for an id that is not a property of the selection.</summary>
/// <param name="PropertyId">The property, as asked or selected.</param>
/// <param name="Name">The name of the property.</param>
/// <param name="City">The city of the property.</param>
/// <param name="Outcome">One of <see cref="BulkApplyOutcomes"/>.</param>
/// <param name="Reason">The code of the rule that refused it (<see cref="BulkApplyOutcomes.Refused"/>); <c>null</c> otherwise.</param>
public sealed record BulkApplyItem(Guid PropertyId, string? Name, string? City, string Outcome, string? Reason);

/// <summary>How many properties are in each outcome.</summary>
public sealed record BulkApplySummary(int Applied, int Unchanged, int Refused, int WouldChange);

/// <summary>The answer of an apply: one item for each property.</summary>
/// <param name="DryRun">True when nothing was written (a dry run, or no confirmation).</param>
/// <param name="ComplianceSender">The sender asked.</param>
/// <param name="Summary">The counts.</param>
/// <param name="Items">The properties by name (then the ids that are not properties of the selection, in the order asked).</param>
public sealed record BulkApplyResult(bool DryRun, ComplianceSenderType ComplianceSender, BulkApplySummary Summary, IReadOnlyList<BulkApplyItem> Items);

/// <summary>
/// How far one flow has got over the properties the scope reaches. The four numbers add up to the properties: <paramref name="Active"/> runs,
/// <paramref name="Ready"/> has every prerequisite met and waits for the activation, <paramref name="ToComplete"/> still lacks something,
/// <paramref name="NotAvailable"/> is a flow that does not apply.
/// </summary>
public sealed record BulkFlowProgress(OnboardingFlow Flow, int Active, int Ready, int ToComplete, int NotAvailable);

/// <summary>«<paramref name="Ready"/> of <paramref name="Total"/> ready», the flows and the pilot group.</summary>
public sealed record BulkProgress(int Total, int Ready, int NotReady, IReadOnlyList<BulkFlowProgress> Flows, BulkPilot Pilot);

/// <summary>A property of the pilot group.</summary>
public sealed record BulkPilotMember(Guid PropertyId, string Name, string City, DateTime AddedAt);

/// <summary>
/// How one flow has got on the pilot group: the members it is <paramref name="Available"/> for, how many of them have it <paramref name="Active"/>,
/// and whether the pilot is active in it (at least one member, and every member it is available for).
/// </summary>
public sealed record BulkPilotFlow(OnboardingFlow Flow, int Available, int Active, bool IsActive);

/// <summary>The pilot group (H10): its members, the recommended and the maximum size, and the state of each flow on it.</summary>
public sealed record BulkPilot(
    IReadOnlyList<BulkPilotMember> Members,
    int Size,
    int RecommendedMin,
    int MaxSize,
    bool BelowRecommended,
    IReadOnlyList<BulkPilotFlow> Flows);

/// <summary>The outcomes of the activation of one flow, as the activation service of T04 reports them.</summary>
public sealed record BulkActivationFlow(OnboardingFlow Flow, IReadOnlyList<FlowActivationOutcome> Outcomes);

/// <summary>The activation of a cohort: one entry for each flow asked, in the order asked.</summary>
public sealed record BulkActivationResult(string Cohort, IReadOnlyList<BulkActivationFlow> Flows);
