using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Models;

public static class OnboardingFlowStates
{
    public const string Active = "active";

    public const string ToComplete = "toComplete";

    public const string NotAvailable = "notAvailable";

    public static IReadOnlyList<string> All { get; } = [Active, ToComplete, NotAvailable];

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

public static class OnboardingFlowReasons
{
    /// <summary>The org never connected to Octorate (in this environment).</summary>
    public const string ConnectionMissing = "connection_missing";

    public const string ConnectionLost = "connection_lost";

    public const string MappingUnconfirmed = "mapping_unconfirmed";

    public const string MappingUnavailable = "mapping_unavailable";

    /// <summary>The property has no stored sender of the Alloggiati and ISTAT communications.</summary>
    public const string SenderMissing = "sender_missing";

    public const string ChannelContentToFix = "channel_content_to_fix";

    public const string CinMissingOrInvalid = "cin_missing_or_invalid";

    public const string TaxRateMissing = "tax_rate_missing";

    public const string MessageSyncNotReady = "message_sync_not_ready";

    public const string IssuerMissing = "issuer_missing";

    public const string NoBookingsYet = "no_bookings_yet";

    public const string FlowNotActivated = "flow_not_activated";

    public const string PropertyLongRent = "property_long_rent";

    public const string PropertyInactive = "property_inactive";

    public const string ContentManagedOnChannel = "content_managed_on_channel";

    /// <summary>
    /// The license of the listing has no chat module (E10/F01/US04/T01): the message sync keeps that as the state of the listing
    /// (<c>OtaMessageSyncState.LastError</c>, <c>OtaMessageSyncErrorCodes.ChatNotLicensed</c>), so the flow says so instead of an empty inbox.
    /// </summary>
    public const string ChatNotLicensed = "chat_not_licensed";

    public const string AccountantHandles = "accountant_handles";

    public const string NotProvided = "not_provided";
}

public static class OnboardingFlowActions
{
    public const string Connect = "connect";

    public const string Reconnect = "reconnect";

    public const string ConfirmMapping = "confirmMapping";

    public const string ChooseSender = "chooseSender";

    public const string FixListing = "fixListing";

    public const string EnterCin = "enterCin";

    public const string EnterTaxRate = "enterTaxRate";

    public const string Activate = "activate";

    public const string ChooseIssuer = "chooseIssuer";

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

public static class OnboardingPrerequisiteKeys
{
    public const string Connection = "connection";

    public const string Mapping = "mapping";

    public const string SingleSender = "singleSender";

    public const string Content = "content";

    public const string Cin = "cin";

    public const string TaxRate = "taxRate";

    public const string MessageSync = "messageSync";

    public const string Issuer = "issuer";

    public const string Bookings = "bookings";

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
    public static FlowPrerequisite Holds(string key) => new(key, true);

    public static FlowPrerequisite Lacks(string key, string reason) => new(key, false, reason);
}

public sealed record FlowReadiness(bool Provided, IReadOnlyList<FlowPrerequisite> Prerequisites, string? NotAvailableReason = null)
{
    public static FlowReadiness NotProvided { get; } = new(false, [], OnboardingFlowReasons.NotProvided);

    public static FlowReadiness NotAvailable(string reason) => new(true, [], reason);

    public static FlowReadiness Needing(params FlowPrerequisite[] prerequisites) => new(true, prerequisites);
}

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
