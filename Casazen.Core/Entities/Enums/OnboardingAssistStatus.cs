namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a «Do it for me» request to CasaZen support stands (NF-107, UX-15). Stored as its number in <c>OnboardingAssistRequests.Status</c>;
/// every member has an explicit value and an existing number never changes. <see cref="Requested"/> and <see cref="InProgress"/> are the
/// two open states: the partial unique index of the table holds one open request for each part and property.
/// </summary>
public enum OnboardingAssistStatus
{
    /// <summary>The host asked and nobody took the request yet.</summary>
    Requested = 1,

    /// <summary>A person of CasaZen support took the request.</summary>
    InProgress = 2,

    /// <summary>Support did the step.</summary>
    Done = 3,

    /// <summary>The request was withdrawn by the host or closed by support without doing the step.</summary>
    Cancelled = 4,
}
