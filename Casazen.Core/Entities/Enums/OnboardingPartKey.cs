namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The eleven parts of the Octorate activation plan (NF-104, ONB-01, E10/F01/US01), in the order the host does them. Stored as its number in
/// <c>OnboardingPartStates.PartKey</c> and <c>OnboardingAssistRequests.PartKey</c>: every member has an explicit value, an existing number never
/// changes and is never reused, and a later part is appended with the next number. Whether a part is open, done or the next one is derived on
/// read from the stored facts of the org (nothing is "done" by a flag, as in the activation checklist of PL-15); only the host's intent to
/// do a part later is stored (<see cref="OnboardingPartStatus"/>).
/// </summary>
public enum OnboardingPartKey
{
    /// <summary>The consent of the org to its Octorate account, or the creation of the properties there.</summary>
    OctorateConnection = 1,

    /// <summary>The mapping of the Octorate properties to the CasaZen ones, and the creation of the missing ones.</summary>
    Properties = 2,

    /// <summary>The connection of each channel, its mapping and the import of its bookings and calendar.</summary>
    Channels = 3,

    /// <summary>The check of the imported content of each channel.</summary>
    Listings = 4,

    /// <summary>The base price, the rate plans, the restrictions and the policies.</summary>
    PricesRules = 5,

    /// <summary>The sender of the Alloggiati and ISTAT communications, the tourist tax and the CIN.</summary>
    Compliance = 6,

    /// <summary>The guest identification, the consent texts, the lock code and the printable QR.</summary>
    Identification = 7,

    /// <summary>The notification preferences, the communication standards and the scheduled messages.</summary>
    MessagesAi = 8,

    /// <summary>Who issues the fiscal documents, the regime, the issuer data and the SDI intermediary.</summary>
    FiscalDocuments = 9,

    /// <summary>The optional marketplace integrations.</summary>
    Partners = 10,

    /// <summary>The summary, the final read-only test and the activation of the flows.</summary>
    Activation = 11,
}
