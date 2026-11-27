namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Who took the decision a <see cref="BiometricVerificationLog"/> row records (E03/F04/US02/T01, ADR-013-R3, NF-50). Stored as an integer:
/// never renumber.
/// </summary>
public enum VerificationMethod
{
    /// <summary>The verification agent compared the selfie with the photo of the document (the rows written before this column are all of this kind).</summary>
    Biometric = 1,

    /// <summary>A person with <c>verification.decision</c> decided from the operator queue.</summary>
    Operator = 2,
}
