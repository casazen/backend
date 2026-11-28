using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Onboarding;

public sealed class FlowSummaryDto
{
    public OnboardingFlow Flow { get; set; }

    public int Active { get; set; }

    public int ToComplete { get; set; }

    public int NotAvailable { get; set; }

    public int Total { get; set; }
}

/// <summary>
/// A prerequisite of a flow on a property and whether it holds (NF-106). <c>key</c>: <c>connection</c>, <c>mapping</c>,
/// <c>singleSender</c>, <c>content</c>, <c>cin</c>, <c>taxRate</c>, <c>messageSync</c>, <c>issuer</c>, <c>bookings</c> (the last three since the flows
/// messages, documents and reports read the facts of their epics, E10/F01/US04/T01; the list only grows). <c>reason</c>: the stable code of why it
/// does not hold, <c>null</c> when it does. Codes only: the client translates them.
/// </summary>
public sealed class FlowPrerequisiteDto
{
    public string Key { get; set; } = string.Empty;

    public bool Met { get; set; }

    public string? Reason { get; set; }
}

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
