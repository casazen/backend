using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The check of one flow in the final test of the activation (ONB-05 «Prova», E10/F01/US01/T04): it passes the booking through the flow and says
/// whether the flow would work, without any effect. A check only reads: no write, no outbox event, no call to Octorate, no send. The flows no epic
/// registered a check for are <see cref="FlowCheckOutcomes.Skipped"/> with <see cref="FlowCheckReasons.NotProvided"/>.
/// </summary>
public interface IFlowDryRunCheck
{
    /// <summary>The flow this check answers for.</summary>
    OnboardingFlow Flow { get; }

    /// <summary>Passes the booking of <paramref name="context"/> through the flow.</summary>
    Task<FlowCheckResult> RunAsync(FlowDryRunContext context, CancellationToken ct);
}

/// <summary>What a check is given: the org, the property and the booking of the test, read without tracking.</summary>
/// <param name="OrgId">The org of the property.</param>
/// <param name="PropertyId">The property.</param>
/// <param name="Booking">The booking, a copy read with <c>AsNoTracking</c>: a change to it is never saved.</param>
public sealed record FlowDryRunContext(Guid OrgId, Guid PropertyId, Booking Booking);

/// <summary>Stable outcomes of a check (cross-repo contract): never rename one.</summary>
public static class FlowCheckOutcomes
{
    /// <summary>The flow would work on the booking; the reasons, if any, are information (guest data still to give before the arrival).</summary>
    public const string Pass = "pass";

    /// <summary>The flow would not work: the reasons say why.</summary>
    public const string Fail = "fail";

    /// <summary>The flow is not checked on this booking: the reason says why.</summary>
    public const string Skipped = "skipped";
}

/// <summary>Stable reasons of the checks: never rename one.</summary>
public static class FlowCheckReasons
{
    /// <summary>No epic registered a check for the flow.</summary>
    public const string NotProvided = "not_provided";

    /// <summary>The property is on no channel.</summary>
    public const string ChannelStatesMissing = "channel_states_missing";

    /// <summary>A channel refused the content of the property.</summary>
    public const string ChannelContentToFix = "channel_content_to_fix";

    /// <summary>Every channel of the property is written on the channel itself: CasaZen sends nothing there.</summary>
    public const string ContentManagedOnChannel = "content_managed_on_channel";

    /// <summary>The state of the booking does not map both ways between CasaZen and Octorate.</summary>
    public const string BookingStatusUnmapped = "booking_status_unmapped";

    /// <summary>A booking of an OTA carries none of the amounts Octorate documents (the commission, what the guest paid).</summary>
    public const string OtaAmountsMissing = "ota_amounts_missing";

    /// <summary>The property has no stored sender of the Alloggiati and ISTAT communications.</summary>
    public const string SenderMissing = "sender_missing";

    /// <summary>The guest data of the stay are not complete yet (information: they are often given at the check-in).</summary>
    public const string GuestDataMissing = "guest_data_missing";

    /// <summary>The booking owes no police communication (a request, a cancelled booking, a no-show).</summary>
    public const string BookingNotActive = "booking_not_active";

    /// <summary>The property is outside Italy: there is no police communication to check.</summary>
    public const string NotApplicable = "not_applicable";
}

/// <summary>The answer of a check: its flow, its outcome and its reason codes. No personal data.</summary>
/// <param name="Flow">The flow.</param>
/// <param name="Outcome">One of <see cref="FlowCheckOutcomes"/>.</param>
/// <param name="Reasons">Codes of <see cref="FlowCheckReasons"/> (or a stable code of the domain error a read gave).</param>
public sealed record FlowCheckResult(OnboardingFlow Flow, string Outcome, IReadOnlyList<string> Reasons)
{
    /// <summary>The flow would work; <paramref name="notes"/> are information.</summary>
    public static FlowCheckResult Pass(OnboardingFlow flow, params string[] notes) => new(flow, FlowCheckOutcomes.Pass, notes);

    /// <summary>The flow would not work, for <paramref name="reasons"/>.</summary>
    public static FlowCheckResult Fail(OnboardingFlow flow, params string[] reasons) => new(flow, FlowCheckOutcomes.Fail, reasons);

    /// <summary>The flow is not checked, for <paramref name="reason"/>.</summary>
    public static FlowCheckResult Skipped(OnboardingFlow flow, string reason) => new(flow, FlowCheckOutcomes.Skipped, [reason]);
}

/// <summary>The answer of the final test: the property, the booking it ran on and one check per flow, in the order of the flows.</summary>
public sealed record FlowTestRunResult(Guid PropertyId, Guid BookingId, IReadOnlyList<FlowCheckResult> Checks);
