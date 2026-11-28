using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory.GuestRegistration;

namespace Casazen.Core.Models;

public static class OnboardingPartStates
{
    public const string Done = "done";

    public const string Open = "open";

    public const string Deferred = "deferred";

    public const string Blocked = "blocked";

    public const string NotAvailable = "notAvailable";
}

public static class OnboardingPartReasons
{
    public const string InventoryScenarioMissing = "inventory_scenario_missing";

    public const string ExistingOtaImportIncomplete = "existing_ota_import_incomplete";

    /// <summary>The org never connected to Octorate in this environment.</summary>
    public const string ConnectionMissing = OnboardingFlowReasons.ConnectionMissing;

    public const string ConnectionLost = OnboardingFlowReasons.ConnectionLost;

    public const string MappingUnavailable = OnboardingFlowReasons.MappingUnavailable;

    public const string MappingUnconfirmed = OnboardingFlowReasons.MappingUnconfirmed;

    public const string ChannelsMissing = "channels_missing";

    public const string ChannelContentToFix = OnboardingFlowReasons.ChannelContentToFix;

    public const string PriceMissing = "price_missing";

    public const string SenderMissing = OnboardingFlowReasons.SenderMissing;

    public const string CinMissingOrInvalid = OnboardingFlowReasons.CinMissingOrInvalid;

    public const string TaxRateMissing = OnboardingFlowReasons.TaxRateMissing;

    public const string IdentificationOff = "identification_off";

    public const string AccessCodeMissing = "access_code_missing";

    public const string FlowsIncomplete = "flows_incomplete";

    public const string NotificationPreferencesMissing = "notification_preferences_missing";

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

    public const string PartnerIntegrationMissing = "partner_integration_missing";

    public const string ConnectorOff = "connector_off";

    public const string SwissMunicipalityMissing = GuestRegistrationReadiness.MunicipalityMissing;

    public const string TicinoStructureIdMissing = GuestRegistrationReadiness.TicinoStructureIdMissing;

    public const string NoSwissProperty = "no_swiss_property";

    public const string NoProperty = "no_property";

    public const string RequiredPartNotDone = "required_part_not_done";

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
    public IReadOnlyList<PartMissingItem> Missing { get; init; } = [];

    public const int MaxMissingItems = 50;

    public bool IsComplete => Provided && Total > 0 && Done >= Total;

    public static PartFacts NotAvailable(string reason) => new(false, 0, 0, reason);

    /// <summary>
    /// <paramref name="done"/> of <paramref name="total"/>, with <paramref name="reason"/> while not all of it is done; with nothing to do the
    /// reason is <see cref="OnboardingPartReasons.NoProperty"/> unless one is given.
    /// </summary>
    public static PartFacts Counted(int done, int total, string? reason) =>
        new(true, done, total, done >= total && total > 0 ? null : reason ?? OnboardingPartReasons.NoProperty);
}

public sealed record PartMissingItem(Guid PropertyId, string Reason, string Code);

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
    string LaunchTarget)
{
    public IReadOnlyList<PartMissingItem> Missing { get; init; } = [];
}

public sealed record OnboardingAssistInfo(Guid Id, OnboardingAssistStatus Status, string Context, DateTime RequestedAt);

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

public sealed record OnboardingPartChange(OnboardingPartKey Key, string Code, string State, DateTime? RemindAt, Guid? TodoId);
