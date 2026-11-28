namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The twelve parts of the Octorate activation plan (NF-104, ONB-01, E10/F01/US01; the twelfth, E29/F02/US01/T02), in the order the host does them. Stored as its number in
/// <c>OnboardingPartStates.PartKey</c> and <c>OnboardingAssistRequests.PartKey</c>: every member has an explicit value, an existing number never
/// changes and is never reused, and a later part is appended with the next number. Whether a part is open, done or the next one is derived on
/// read from the stored facts of the org (nothing is "done" by a flag, as in the activation checklist of PL-15); only the host's intent to
/// do a part later is stored (<see cref="OnboardingPartStatus"/>).
/// </summary>
public enum OnboardingPartKey
{
    OctorateConnection = 1,

    Properties = 2,

    Channels = 3,

    Listings = 4,

    PricesRules = 5,

    /// <summary>The sender of the Alloggiati and ISTAT communications, the tourist tax and the CIN.</summary>
    Compliance = 6,

    Identification = 7,

    MessagesAi = 8,

    /// <summary>Who issues the fiscal documents, the regime, the issuer data and the SDI intermediary.</summary>
    FiscalDocuments = 9,

    Partners = 10,

    Activation = 11,

    GuestRegistration = 12,
}
