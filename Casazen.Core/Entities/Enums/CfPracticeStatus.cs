namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Status values of a ConcordatoFacile practice. The API serializes the <b>names</b> and unknown values from the partner
/// API are mapped to <see cref="Unknown"/> (ADR-008-R13, ADR-008-R21). Never reorder, only append.
/// </summary>
public enum CfPracticeStatus
{
    Received = 0,

    InProgress = 1,

    AwaitingUser = 2,

    DraftSent = 3,

    SignedReceived = 4,

    SentToAssociation = 5,

    Attested = 6,

    Registered = 7,

    ClosedWithoutAttestation = 8,

    Suspended = 9,

    Unknown = 10,
}
