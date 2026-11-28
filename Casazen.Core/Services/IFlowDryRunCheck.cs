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
    OnboardingFlow Flow { get; }

    Task<FlowCheckResult> RunAsync(FlowDryRunContext context, CancellationToken ct);
}

/// <summary>What a check is given: the org, the property and the booking of the test, read without tracking.</summary>
/// <param name="OrgId">The org of the property.</param>
/// <param name="PropertyId">The property.</param>
/// <param name="Booking">The booking, a copy read with <c>AsNoTracking</c>: a change to it is never saved.</param>
public sealed record FlowDryRunContext(Guid OrgId, Guid PropertyId, Booking Booking);

public static class FlowCheckOutcomes
{
    public const string Pass = "pass";

    public const string Fail = "fail";

    public const string Skipped = "skipped";
}

public static class FlowCheckReasons
{
    public const string NotProvided = "not_provided";

    public const string ChannelStatesMissing = "channel_states_missing";

    public const string ChannelContentToFix = "channel_content_to_fix";

    public const string ContentManagedOnChannel = "content_managed_on_channel";

    public const string BookingStatusUnmapped = "booking_status_unmapped";

    public const string OtaAmountsMissing = "ota_amounts_missing";

    /// <summary>The property has no stored sender of the Alloggiati and ISTAT communications.</summary>
    public const string SenderMissing = "sender_missing";

    public const string GuestDataMissing = "guest_data_missing";

    public const string BookingNotActive = "booking_not_active";

    public const string NotApplicable = "not_applicable";
}

/// <summary>The answer of a check: its flow, its outcome and its reason codes. No personal data.</summary>
/// <param name="Flow">The flow.</param>
/// <param name="Outcome">One of <see cref="FlowCheckOutcomes"/>.</param>
/// <param name="Reasons">Codes of <see cref="FlowCheckReasons"/> (or a stable code of the domain error a read gave).</param>
public sealed record FlowCheckResult(OnboardingFlow Flow, string Outcome, IReadOnlyList<string> Reasons)
{
    public static FlowCheckResult Pass(OnboardingFlow flow, params string[] notes) => new(flow, FlowCheckOutcomes.Pass, notes);

    public static FlowCheckResult Fail(OnboardingFlow flow, params string[] reasons) => new(flow, FlowCheckOutcomes.Fail, reasons);

    public static FlowCheckResult Skipped(OnboardingFlow flow, string reason) => new(flow, FlowCheckOutcomes.Skipped, [reason]);
}

public sealed record FlowTestRunResult(Guid PropertyId, Guid BookingId, IReadOnlyList<FlowCheckResult> Checks);
