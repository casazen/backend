using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Models;

/// <summary>
/// Stable states of a part of the activation plan (NF-104, E10/F01/US01/T03): never rename one. Derived on every read, in this precedence:
/// <see cref="NotAvailable"/>, <see cref="Done"/>, <see cref="Deferred"/>, <see cref="Blocked"/>, <see cref="Open"/>.
/// </summary>
public static class OnboardingPartStates
{
    /// <summary>The stored facts prove the part is complete.</summary>
    public const string Done = "done";

    /// <summary>Nothing stops the host from doing the part now.</summary>
    public const string Open = "open";

    /// <summary>The host chose «Do it later»: a "To do" stands for it.</summary>
    public const string Deferred = "deferred";

    /// <summary>A part this one requires is not done yet.</summary>
    public const string Blocked = "blocked";

    /// <summary>CasaZen has nothing to do the part with (yet), or the part does not apply: the reason says why.</summary>
    public const string NotAvailable = "notAvailable";
}

/// <summary>Stable reasons a part of the plan is not done (NF-104): never rename one. Codes only, the client translates them.</summary>
public static class OnboardingPartReasons
{
    /// <summary>A property (or a group of the portfolio) has no declared inventory scenario (<c>onOctorate</c>, <c>onOtas</c>, <c>newInventory</c>).</summary>
    public const string InventoryScenarioMissing = "inventory_scenario_missing";

    /// <summary>The import of the listings that already exist on the OTAs has not reached the mapping yet (D6).</summary>
    public const string ExistingOtaImportIncomplete = "existing_ota_import_incomplete";

    /// <summary>The org never connected to Octorate in this environment.</summary>
    public const string ConnectionMissing = OnboardingFlowReasons.ConnectionMissing;

    /// <summary>The connection exists and no longer works.</summary>
    public const string ConnectionLost = OnboardingFlowReasons.ConnectionLost;

    /// <summary>CasaZen could not read the mapping: never presumed confirmed.</summary>
    public const string MappingUnavailable = OnboardingFlowReasons.MappingUnavailable;

    /// <summary>A property has no confirmed listing.</summary>
    public const string MappingUnconfirmed = OnboardingFlowReasons.MappingUnconfirmed;

    /// <summary>A property is on no channel yet.</summary>
    public const string ChannelsMissing = "channels_missing";

    /// <summary>A channel refused the content of a property.</summary>
    public const string ChannelContentToFix = OnboardingFlowReasons.ChannelContentToFix;

    /// <summary>A property has no base nightly price.</summary>
    public const string PriceMissing = "price_missing";

    /// <summary>A property has no stored sender of the Alloggiati and ISTAT communications.</summary>
    public const string SenderMissing = OnboardingFlowReasons.SenderMissing;

    /// <summary>A property has no valid CIN.</summary>
    public const string CinMissingOrInvalid = OnboardingFlowReasons.CinMissingOrInvalid;

    /// <summary>CasaZen has no tourist tax rate in force for the comune of a property.</summary>
    public const string TaxRateMissing = OnboardingFlowReasons.TaxRateMissing;

    /// <summary>The identification of the guests is switched off in this installation (<c>Features:CheckInIdentityVerification</c>).</summary>
    public const string IdentificationOff = "identification_off";

    /// <summary>A property has no access code.</summary>
    public const string AccessCodeMissing = "access_code_missing";

    /// <summary>A flow is still to complete on a property.</summary>
    public const string FlowsIncomplete = "flows_incomplete";

    /// <summary>No administrator of the organization has saved its notification preferences yet (E10/F01/US04/T01, part «Messages and AI»).</summary>
    public const string NotificationPreferencesMissing = "notification_preferences_missing";

    /// <summary>
    /// The assistant is switched on in this installation and the organization has configured none of its situations (E10/F01/US04/T01, part
    /// «Messages and AI»).
    /// </summary>
    public const string AssistantSituationMissing = "assistant_situation_missing";

    /// <summary>
    /// Part «Fatturazione» (E10/F01/US04/T01, E11/F01/US01/T04): the issuance is switched off (<c>Features:FiscalDocumentIssuance</c>), so nobody issues and the
    /// wizard is closed. With the issuance on the reason of the part is never this one but the key of the first missing item of the billing setup
    /// (<see cref="BillingSetupItemKeys"/>: <c>legal_name_missing</c>, <c>regime_missing</c>, <c>guest_series_unconfirmed</c>...), which the client maps to
    /// the step of the wizard to reopen.
    /// </summary>
    public const string IssuerMissing = OnboardingFlowReasons.IssuerMissing;

    /// <summary>
    /// Retired on 2026-11-14 (E11/F01/US01/T01, E11/F01/US01/T04): CasaZen is the only issuer and the accountant only receives the exports, so no part is
    /// «done because my accountant issues» any more. The constant stays for the rows and the clients that still know the code; nothing produces it.
    /// </summary>
    public const string AccountantHandles = OnboardingFlowReasons.AccountantHandles;

    /// <summary>The organization has no integration active or declared in the marketplace (E10/F01/US04/T01, part «Partners», optional).</summary>
    public const string PartnerIntegrationMissing = "partner_integration_missing";

    /// <summary>
    /// The Octorate connector is switched off in this installation (<c>Features:OctorateConnector</c>), and with it the marketplace endpoints the
    /// part «Partners» reads (E10/F01/US04/T01).
    /// </summary>
    public const string ConnectorOff = "connector_off";

    /// <summary>The caller reaches no short-rent property that is active: there is nothing to count yet.</summary>
    public const string NoProperty = "no_property";

    /// <summary>A part this one requires is not done yet (the reason of a <see cref="OnboardingPartStates.Blocked"/> part).</summary>
    public const string RequiredPartNotDone = "required_part_not_done";

    /// <summary>Nobody provides the part yet: its epic has not registered its evaluator.</summary>
    public const string NotProvided = OnboardingFlowReasons.NotProvided;
}

/// <summary>
/// What an evaluator says about a part from the stored facts (NF-104): whether CasaZen provides it at all, how much of it is done over how much
/// there is to do, and the reason it is not done. Counts and a code: no personal data.
/// </summary>
/// <param name="Provided">False when the part cannot be done with what CasaZen has (the part is <see cref="OnboardingPartStates.NotAvailable"/>).</param>
/// <param name="Done">How many of the things to do are done (properties, assignments...).</param>
/// <param name="Total">How many there are to do.</param>
/// <param name="Reason">One of <see cref="OnboardingPartReasons"/> when the part is not complete; <c>null</c> when it is.</param>
public sealed record PartFacts(bool Provided, int Done, int Total, string? Reason)
{
    /// <summary>True when the part is provided, has something to do, and all of it is done: the only way a part is done.</summary>
    public bool IsComplete => Provided && Total > 0 && Done >= Total;

    /// <summary>The part cannot be done with what CasaZen has, for <paramref name="reason"/>.</summary>
    public static PartFacts NotAvailable(string reason) => new(false, 0, 0, reason);

    /// <summary>
    /// <paramref name="done"/> of <paramref name="total"/>, with <paramref name="reason"/> while not all of it is done; with nothing to do the
    /// reason is <see cref="OnboardingPartReasons.NoProperty"/> unless one is given.
    /// </summary>
    public static PartFacts Counted(int done, int total, string? reason) =>
        new(true, done, total, done >= total && total > 0 ? null : reason ?? OnboardingPartReasons.NoProperty);
}

/// <summary>The plan of the activation of an org (NF-104, <c>GET api/onboarding/plan</c>): the eleven parts, «What you need», the path and whether the org is activated.</summary>
/// <param name="Parts">The eleven parts in the order of the plan.</param>
/// <param name="Needs">The five codes of «What you need» (<c>OnboardingNeedCodes.All</c>).</param>
/// <param name="Path">The connected path of UX-10.</param>
/// <param name="Activated">True when the part <see cref="OnboardingPartKey.Activation"/> is done: every property reached has every available flow active.</param>
public sealed record OnboardingPlan(
    IReadOnlyList<OnboardingPlanPart> Parts,
    IReadOnlyList<string> Needs,
    OnboardingPath Path,
    bool Activated);

/// <summary>One part of the plan as the caller sees it (NF-104). Codes, counts and ids only.</summary>
/// <param name="Key">The part.</param>
/// <param name="Code">Its stable code.</param>
/// <param name="Order">Its place in the plan.</param>
/// <param name="State">One of <see cref="OnboardingPartStates"/>.</param>
/// <param name="Reason">One of <see cref="OnboardingPartReasons"/>; <c>null</c> for a part that is done.</param>
/// <param name="Done">How much is done.</param>
/// <param name="Total">How much there is to do.</param>
/// <param name="Requires">The codes of the parts it requires.</param>
/// <param name="Deferrable">False for a part another part requires.</param>
/// <param name="Needs">The codes of «What you need» of the part.</param>
/// <param name="RemindAt">When the "To do" of a deferral or an exit is due (UTC); <c>null</c> when there is none.</param>
/// <param name="Assist">The open «Do it for me» request of the part, if any.</param>
/// <param name="LaunchTarget">The code of the wizard that does the part.</param>
public sealed record OnboardingPlanPart(
    OnboardingPartKey Key,
    string Code,
    int Order,
    string State,
    string? Reason,
    int Done,
    int Total,
    IReadOnlyList<string> Requires,
    bool Deferrable,
    IReadOnlyList<string> Needs,
    DateTime? RemindAt,
    OnboardingAssistInfo? Assist,
    string LaunchTarget);

/// <summary>An open «Do it for me» request of a part (NF-107): its id, where it stands, what it is about and when it was asked. No person, no note.</summary>
public sealed record OnboardingAssistInfo(Guid Id, OnboardingAssistStatus Status, string Context, DateTime RequestedAt);

/// <summary>The connected path of UX-10 (NF-10): its steps mapped to the parts of the plan and the next step to do.</summary>
/// <param name="Code">The code of the path.</param>
/// <param name="Steps">The steps in order.</param>
/// <param name="Next">
/// The code of the first step that is visible and that the host did not put off (a step in state <see cref="OnboardingPartStates.Deferred"/> stays
/// visible but is not proposed again); <c>null</c> when there is none.
/// </param>
public sealed record OnboardingPath(string Code, IReadOnlyList<OnboardingPathStep> Steps, string? Next);

/// <summary>One step of the path (NF-10).</summary>
/// <param name="Code">The stable code of the step.</param>
/// <param name="Order">Its place in the path.</param>
/// <param name="Parts">The codes of the parts it is made of.</param>
/// <param name="State">
/// <see cref="OnboardingPartStates.Done"/> when every part is done, <see cref="OnboardingPartStates.NotAvailable"/> when every part is not
/// available, otherwise the state of its first part that is neither.
/// </param>
/// <param name="Visible">True only while the step is neither done nor not available: there is something to do in it.</param>
public sealed record OnboardingPathStep(string Code, int Order, IReadOnlyList<string> Parts, string State, bool Visible);

/// <summary>What a deferral, an exit or a resume left on a part (NF-104).</summary>
/// <param name="Key">The part.</param>
/// <param name="Code">Its stable code.</param>
/// <param name="State"><see cref="OnboardingPartStates.Deferred"/> after a deferral, <see cref="OnboardingPartStates.Open"/> after an exit or a resume.</param>
/// <param name="RemindAt">When the "To do" is due (UTC); <c>null</c> after a resume.</param>
/// <param name="TodoId">The open "To do" that stands for the part; <c>null</c> after a resume.</param>
public sealed record OnboardingPartChange(OnboardingPartKey Key, string Code, string State, DateTime? RemindAt, Guid? TodoId);
