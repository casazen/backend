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

    Task<BulkProgress> GetProgressAsync(HostScope scope, CancellationToken ct);

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

public static class BulkSelectionKinds
{
    public const string All = "all";

    public const string Ids = "ids";

    public const string Filter = "filter";

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

public static class BulkCohorts
{
    public const string Pilot = "pilot";

    public const string Rest = "rest";

    public static string? Parse(string? value)
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, Pilot, StringComparison.OrdinalIgnoreCase))
            return Pilot;

        return string.Equals(trimmed, Rest, StringComparison.OrdinalIgnoreCase) ? Rest : null;
    }
}

public static class BulkApplyOutcomes
{
    public const string Applied = "applied";

    public const string Unchanged = "unchanged";

    public const string Refused = "refused";

    public const string WouldChange = "wouldChange";
}

public static class OnboardingBulkCodes
{
    public const string SelectionInvalid = "onboarding_selection_invalid";

    public const string SelectionTooLarge = "onboarding_selection_too_large";

    public const string ChoiceInvalid = "onboarding_choice_invalid";

    public const string PilotGroupTooLarge = "pilot_group_too_large";

    public const string PilotGroupNotActive = "pilot_group_not_active";

    public const string PilotGroupEmpty = "onboarding_pilot_group_empty";

    public const string CohortInvalid = "onboarding_cohort_invalid";

    public const string FlowsEmpty = "onboarding_flows_empty";

    public const string PropertyNotFound = "property_not_found";
}

public static class OnboardingBulkLimits
{
    public const int SampleSize = 20;

    public const int PilotRecommendedMin = 5;

    public const int DefaultPilotGroupMaxSize = 10;
}

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

public sealed record BulkFlowStateFilter(OnboardingFlow Flow, string State);

/// <summary>The choice to apply: the sender of the Alloggiati Web and ISTAT communications, and with it, once for the org, whether CasaZen writes the guests to the channel manager on their own.</summary>
/// <param name="ComplianceSender">The sender: <c>ChannelManager</c>, <c>CasaZen</c> or <c>Manual</c>; the switch of E03 decides which one a property may have.</param>
/// <param name="AutoSendGuestsToOctorate">An org-wide setting (E20/F01/US01/T01, decision O1), written once and not for each property, only with a confirmed apply: <c>true</c> lets CasaZen write the guests of a stay to the channel manager as soon as their data is complete and the identity check passed; <c>null</c> leaves it as it is.</param>
public sealed record BulkChoice(ComplianceSenderType? ComplianceSender, bool? AutoSendGuestsToOctorate = null);

public sealed record BulkPropertySample(Guid PropertyId, string Name, string City);

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

public sealed record BulkApplySummary(int Applied, int Unchanged, int Refused, int WouldChange);

public sealed record BulkApplyResult(
    bool DryRun,
    ComplianceSenderType ComplianceSender,
    BulkApplySummary Summary,
    IReadOnlyList<BulkApplyItem> Items,
    bool? AutoSendGuestsToOctorate = null);

public sealed record BulkFlowProgress(OnboardingFlow Flow, int Active, int Ready, int ToComplete, int NotAvailable);

public sealed record BulkProgress(int Total, int Ready, int NotReady, IReadOnlyList<BulkFlowProgress> Flows, BulkPilot Pilot);

public sealed record BulkPilotMember(Guid PropertyId, string Name, string City, DateTime AddedAt);

public sealed record BulkPilotFlow(OnboardingFlow Flow, int Available, int Active, bool IsActive);

public sealed record BulkPilot(
    IReadOnlyList<BulkPilotMember> Members,
    int Size,
    int RecommendedMin,
    int MaxSize,
    bool BelowRecommended,
    IReadOnlyList<BulkPilotFlow> Flows);

public sealed record BulkActivationFlow(OnboardingFlow Flow, IReadOnlyList<FlowActivationOutcome> Outcomes);

public sealed record BulkActivationResult(string Cohort, IReadOnlyList<BulkActivationFlow> Flows);
