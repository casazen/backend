using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Models;

/// <summary>
/// Stable states of a flow on a property (NF-106, ONB-03): never rename one. The client translates the codes, the server never sends a
/// sentence.
/// </summary>
public static class OnboardingFlowStates
{
    /// <summary>The flow runs: it was activated and every prerequisite still holds.</summary>
    public const string Active = "active";

    /// <summary>Something is missing: the reason says what and the action says what fixes it.</summary>
    public const string ToComplete = "toComplete";

    /// <summary>The flow does not apply to the property (or CasaZen has nothing to run for it yet): the reason says why.</summary>
    public const string NotAvailable = "notAvailable";

    /// <summary>Every state, in the order the summary counts them.</summary>
    public static IReadOnlyList<string> All { get; } = [Active, ToComplete, NotAvailable];

    /// <summary>
    /// The state named by <paramref name="value"/> (case-insensitive, once trimmed), as the stable code; false for a blank text or a name that
    /// is not a state. Used for the <c>state</c> filter of the list.
    /// </summary>
    public static bool TryParse(string? value, out string state)
    {
        state = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        foreach (var known in All)
        {
            if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                state = known;
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Stable reasons a flow is not active on a property (NF-106): never rename one. The first group is a prerequisite that does not hold
/// («to complete»), the second says the flow does not apply («not available»).
/// </summary>
public static class OnboardingFlowReasons
{
    /// <summary>The org never connected to Octorate (in this environment).</summary>
    public const string ConnectionMissing = "connection_missing";

    /// <summary>The connection exists and no longer works: the host disconnected it, or Octorate refused the token.</summary>
    public const string ConnectionLost = "connection_lost";

    /// <summary>The listing is not confirmed to the property: it is proposed, ambiguous, rejected or not found.</summary>
    public const string MappingUnconfirmed = "mapping_unconfirmed";

    /// <summary>CasaZen could not read the mapping: it is not met, never presumed met.</summary>
    public const string MappingUnavailable = "mapping_unavailable";

    /// <summary>The property has no stored sender of the Alloggiati and ISTAT communications.</summary>
    public const string SenderMissing = "sender_missing";

    /// <summary>A channel refused the content of the property, or something is missing on it.</summary>
    public const string ChannelContentToFix = "channel_content_to_fix";

    /// <summary>The CIN of the property is missing or not valid.</summary>
    public const string CinMissingOrInvalid = "cin_missing_or_invalid";

    /// <summary>CasaZen has no tourist tax rate in force today for the comune of the property.</summary>
    public const string TaxRateMissing = "tax_rate_missing";

    /// <summary>
    /// The copy of the conversations of the listing has not worked yet (E10/F01/US04/T01): no pass of the message sync has read the listing, or the
    /// last one failed (<c>OtaMessageSyncState.LastError</c>). The host cannot fix it: the sync runs by itself and tries again.
    /// </summary>
    public const string MessageSyncNotReady = "message_sync_not_ready";

    /// <summary>The organization has not chosen who issues its fiscal documents (E10/F01/US04/T01, E06): the host chooses in the issuer wizard.</summary>
    public const string IssuerMissing = "issuer_missing";

    /// <summary>The property has no booking imported from the channel manager yet (E10/F01/US04/T01): there is nothing to report on.</summary>
    public const string NoBookingsYet = "no_bookings_yet";

    /// <summary>Every prerequisite holds and the host did not activate the flow on the property yet.</summary>
    public const string FlowNotActivated = "flow_not_activated";

    /// <summary>The property is in long-term mode: the flows of the short stays do not apply to it.</summary>
    public const string PropertyLongRent = "property_long_rent";

    /// <summary>The property is not active.</summary>
    public const string PropertyInactive = "property_inactive";

    /// <summary>The content of the property is written on the channel itself (Expedia): CasaZen sends nothing to it.</summary>
    public const string ContentManagedOnChannel = "content_managed_on_channel";

    /// <summary>
    /// The license of the listing has no chat module (E10/F01/US04/T01): the message sync keeps that as the state of the listing
    /// (<c>OtaMessageSyncState.LastError</c>, <c>OtaMessageSyncErrorCodes.ChatNotLicensed</c>), so the flow says so instead of an empty inbox.
    /// </summary>
    public const string ChatNotLicensed = "chat_not_licensed";

    /// <summary>The organization chose «none, my accountant issues» (E10/F01/US04/T01, E06): CasaZen has no document to run for it.</summary>
    public const string AccountantHandles = "accountant_handles";

    /// <summary>CasaZen has nothing to run for this flow (yet): no provider answers for it.</summary>
    public const string NotProvided = "not_provided";
}

/// <summary>
/// Stable actions that fix a reason, the «Do now» of the host (NF-106): never rename one. A reason may have none (the host cannot fix it, as
/// <see cref="OnboardingFlowReasons.MappingUnavailable"/>).
/// </summary>
public static class OnboardingFlowActions
{
    /// <summary>Connect the org to Octorate.</summary>
    public const string Connect = "connect";

    /// <summary>Connect the org to Octorate again.</summary>
    public const string Reconnect = "reconnect";

    /// <summary>Confirm the mapping of the listing to the property.</summary>
    public const string ConfirmMapping = "confirmMapping";

    /// <summary>Choose who sends the Alloggiati and ISTAT communications of the property.</summary>
    public const string ChooseSender = "chooseSender";

    /// <summary>Fix the content of the property on the channel.</summary>
    public const string FixListing = "fixListing";

    /// <summary>Enter a valid CIN.</summary>
    public const string EnterCin = "enterCin";

    /// <summary>Enter the tourist tax rate of the comune.</summary>
    public const string EnterTaxRate = "enterTaxRate";

    /// <summary>Activate the flow on the property.</summary>
    public const string Activate = "activate";

    /// <summary>Choose who issues the fiscal documents of the organization: it opens the issuer wizard of E06 (E10/F01/US04/T01).</summary>
    public const string ChooseIssuer = "chooseIssuer";

    /// <summary>
    /// The action that fixes <paramref name="reason"/>; <c>null</c> when the host cannot fix it or the reason is not one of the unmet
    /// prerequisites (a «not available» reason, a reason this version does not know). The host cannot fix
    /// <see cref="OnboardingFlowReasons.MessageSyncNotReady"/> (the sync runs by itself) nor <see cref="OnboardingFlowReasons.NoBookingsYet"/> (it
    /// ends when the first booking is imported).
    /// </summary>
    public static string? ForReason(string? reason) => reason switch
    {
        OnboardingFlowReasons.ConnectionMissing => Connect,
        OnboardingFlowReasons.ConnectionLost => Reconnect,
        OnboardingFlowReasons.MappingUnconfirmed => ConfirmMapping,
        OnboardingFlowReasons.SenderMissing => ChooseSender,
        OnboardingFlowReasons.ChannelContentToFix => FixListing,
        OnboardingFlowReasons.CinMissingOrInvalid => EnterCin,
        OnboardingFlowReasons.TaxRateMissing => EnterTaxRate,
        OnboardingFlowReasons.IssuerMissing => ChooseIssuer,
        OnboardingFlowReasons.FlowNotActivated => Activate,
        _ => null,
    };
}

/// <summary>Stable keys of the prerequisites of a flow (NF-106): never rename one.</summary>
public static class OnboardingPrerequisiteKeys
{
    /// <summary>The org's Octorate connection of this environment is connected.</summary>
    public const string Connection = "connection";

    /// <summary>The listing of the property is confirmed on the channel manager.</summary>
    public const string Mapping = "mapping";

    /// <summary>The property has one sender of the police and ISTAT communications.</summary>
    public const string SingleSender = "singleSender";

    /// <summary>No channel refused the content of the property.</summary>
    public const string Content = "content";

    /// <summary>The property has a valid CIN.</summary>
    public const string Cin = "cin";

    /// <summary>CasaZen has a tourist tax rate in force for the comune of the property.</summary>
    public const string TaxRate = "taxRate";

    /// <summary>The message sync has read the listing of the property and its last pass worked (E10/F01/US04/T01).</summary>
    public const string MessageSync = "messageSync";

    /// <summary>The organization chose who issues its fiscal documents: an issuer profile exists (E10/F01/US04/T01, E06).</summary>
    public const string Issuer = "issuer";

    /// <summary>The property has at least one active booking imported from the channel manager (E10/F01/US04/T01).</summary>
    public const string Bookings = "bookings";

    /// <summary>
    /// The reason an unmet prerequisite stands for when its provider did not give one: every prerequisite has one reason, so a provider that
    /// forgets it never turns an unmet prerequisite into a met one. <c>null</c> for a key this version does not know.
    /// </summary>
    public static string? DefaultReasonOf(string? key) => key switch
    {
        Connection => OnboardingFlowReasons.ConnectionMissing,
        Mapping => OnboardingFlowReasons.MappingUnavailable,
        SingleSender => OnboardingFlowReasons.SenderMissing,
        Content => OnboardingFlowReasons.ChannelContentToFix,
        Cin => OnboardingFlowReasons.CinMissingOrInvalid,
        TaxRate => OnboardingFlowReasons.TaxRateMissing,
        MessageSync => OnboardingFlowReasons.MessageSyncNotReady,
        Issuer => OnboardingFlowReasons.IssuerMissing,
        Bookings => OnboardingFlowReasons.NoBookingsYet,
        _ => null,
    };
}

/// <summary>
/// How many properties are in each state of one flow (NF-106), over the properties the caller reaches.
/// <paramref name="Total"/> is the sum of the three: every property the caller reaches is in exactly one state of each flow.
/// </summary>
/// <param name="Flow">The flow.</param>
/// <param name="Active">Properties where the flow runs.</param>
/// <param name="ToComplete">Properties where something is missing.</param>
/// <param name="NotAvailable">Properties the flow does not apply to.</param>
/// <param name="Total">All the properties the caller reaches.</param>
public sealed record FlowSummary(OnboardingFlow Flow, int Active, int ToComplete, int NotAvailable, int Total);

/// <summary>
/// One prerequisite of a flow on a property and whether it holds (NF-106). No personal data: a key, a flag and a reason code.
/// </summary>
/// <param name="Key">One of <see cref="OnboardingPrerequisiteKeys"/>.</param>
/// <param name="Met">True when the prerequisite holds.</param>
/// <param name="Reason">One of <see cref="OnboardingFlowReasons"/> when it does not hold; <c>null</c> when it does.</param>
public sealed record FlowPrerequisite(string Key, bool Met, string? Reason = null)
{
    /// <summary>The prerequisite <paramref name="key"/>, met.</summary>
    public static FlowPrerequisite Holds(string key) => new(key, true);

    /// <summary>The prerequisite <paramref name="key"/>, not met for <paramref name="reason"/>.</summary>
    public static FlowPrerequisite Lacks(string key, string reason) => new(key, false, reason);
}

/// <summary>
/// What a provider says about one property for one flow (NF-106): whether CasaZen has a provider for the flow at all, the prerequisites
/// it checked, and, when the flow cannot apply to the property whatever the host does, why.
/// </summary>
/// <param name="Provided">False when nobody answers for the flow: the flow is not available (<see cref="OnboardingFlowReasons.NotProvided"/>).</param>
/// <param name="Prerequisites">What the flow needs, in the order they are checked; the first one that does not hold is the reason shown.</param>
/// <param name="NotAvailableReason">
/// A reason the flow does not apply to the property (<see cref="OnboardingFlowReasons.ContentManagedOnChannel"/>); <c>null</c> when it does.
/// </param>
public sealed record FlowReadiness(bool Provided, IReadOnlyList<FlowPrerequisite> Prerequisites, string? NotAvailableReason = null)
{
    /// <summary>Nobody answers for the flow.</summary>
    public static FlowReadiness NotProvided { get; } = new(false, [], OnboardingFlowReasons.NotProvided);

    /// <summary>The flow does not apply to the property for <paramref name="reason"/>.</summary>
    public static FlowReadiness NotAvailable(string reason) => new(true, [], reason);

    /// <summary>The flow applies and needs <paramref name="prerequisites"/>.</summary>
    public static FlowReadiness Needing(params FlowPrerequisite[] prerequisites) => new(true, prerequisites);
}

/// <summary>
/// The facts about a property the rules of the flow status read (NF-106), loaded by the service so the rules stay pure.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Name">The name of the property.</param>
/// <param name="City">The city of the property.</param>
/// <param name="RentalMode">Short or long: only the short-rent properties run the flows.</param>
/// <param name="IsActive">False for a property the host switched off.</param>
public sealed record FlowPropertyFacts(Guid PropertyId, string Name, string City, RentalMode RentalMode, bool IsActive);

/// <summary>
/// One flow on one property (NF-106): its state, why, what fixes it, and what the flow needs with what holds. All stable codes: the client
/// translates them. No personal data.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Name">The name of the property.</param>
/// <param name="City">The city of the property.</param>
/// <param name="Flow">The flow.</param>
/// <param name="State">One of <see cref="OnboardingFlowStates"/>.</param>
/// <param name="Reason">One of <see cref="OnboardingFlowReasons"/> when the state is not <c>active</c>; <c>null</c> when it is.</param>
/// <param name="Action">One of <see cref="OnboardingFlowActions"/>: the «Do now» of a <c>toComplete</c> state; <c>null</c> when nothing the host does fixes it.</param>
/// <param name="Prerequisites">The prerequisites of the flow on the property, met or not; empty when the flow does not apply to the property.</param>
public sealed record FlowPropertyStatus(
    Guid PropertyId,
    string Name,
    string City,
    OnboardingFlow Flow,
    string State,
    string? Reason,
    string? Action,
    IReadOnlyList<FlowPrerequisite> Prerequisites);
