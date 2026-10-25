namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Status values of a ConcordatoFacile practice. The API serializes the <b>names</b> and unknown values from the partner
/// API are mapped to <see cref="Unknown"/> (ADR-008-R13, ADR-008-R21). Never reorder, only append.
/// </summary>
public enum CfPracticeStatus
{
    /// <summary>Practice received by ConcordatoFacile.</summary>
    Received = 0,

    /// <summary>Practice being processed.</summary>
    InProgress = 1,

    /// <summary>Awaiting action from the user.</summary>
    AwaitingUser = 2,

    /// <summary>Draft sent to the user.</summary>
    DraftSent = 3,

    /// <summary>Signed document received from user.</summary>
    SignedReceived = 4,

    /// <summary>Sent to the landlord association.</summary>
    SentToAssociation = 5,

    /// <summary>Attested by the association.</summary>
    Attested = 6,

    /// <summary>Registered successfully.</summary>
    Registered = 7,

    /// <summary>Closed without attestation.</summary>
    ClosedWithoutAttestation = 8,

    /// <summary>Practice suspended.</summary>
    Suspended = 9,

    /// <summary>Unknown status value from the CF API (mapped to preserve forward compatibility).</summary>
    Unknown = 10,
}
