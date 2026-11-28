namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The six flows CasaZen runs for a property once it works with Octorate (NF-106, ONB-03): each one is active, to complete or not available
/// for each property. Stored as its number in <c>PropertyFlowActivations.Flow</c>; every member has an explicit value, an existing number
/// never changes and is never reused. The API speaks the member name (<c>api/onboarding/flows/{flow}/properties</c>).
/// </summary>
public enum OnboardingFlow
{
    Listings = 1,

    Bookings = 2,

    /// <summary>The check-in of the guests and the Alloggiati, ISTAT and tourist tax obligations.</summary>
    CheckInCompliance = 3,

    Messages = 4,

    Documents = 5,

    Reports = 6,
}
