using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Onboarding;

/// <summary>
/// How many properties are in each state of one flow, over the properties the caller reaches (<c>flows</c> of
/// <c>GET api/onboarding/status</c>, NF-106). <c>total</c> is the sum of the other three.
/// </summary>
public sealed class FlowSummaryDto
{
    /// <summary>The flow, by its member name: <c>Listings</c>, <c>Bookings</c>, <c>CheckInCompliance</c>, <c>Messages</c>, <c>Documents</c>, <c>Reports</c>.</summary>
    public OnboardingFlow Flow { get; set; }

    public int Active { get; set; }

    public int ToComplete { get; set; }

    public int NotAvailable { get; set; }

    public int Total { get; set; }
}

/// <summary>
/// A prerequisite of a flow on a property and whether it holds (NF-106). <c>key</c>: <c>connection</c>, <c>mapping</c>,
/// <c>singleSender</c>, <c>content</c>, <c>cin</c>, <c>taxRate</c>. <c>reason</c>: the stable code of why it does not hold, <c>null</c>
/// when it does. Codes only: the client translates them.
/// </summary>
public sealed class FlowPrerequisiteDto
{
    public string Key { get; set; } = string.Empty;

    public bool Met { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// One flow on one property (<c>GET api/onboarding/flows/{flow}/properties</c>, NF-106). <c>state</c>: <c>active</c>, <c>toComplete</c> or
/// <c>notAvailable</c>. <c>reason</c>: the stable code of what is missing or why the flow does not apply, <c>null</c> when it is active.
/// <c>action</c>: the stable code of what fixes it («Do now»), <c>null</c> when nothing the host does does. <c>prerequisites</c>: what the
/// flow needs on the property, met or not (empty when the flow does not apply to it). Codes only: the client translates them.
/// </summary>
public sealed class FlowPropertyStatusDto
{
    public Guid PropertyId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public OnboardingFlow Flow { get; set; }

    public string State { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public string? Action { get; set; }

    public List<FlowPrerequisiteDto> Prerequisites { get; set; } = [];
}
