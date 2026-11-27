namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How the decision a <see cref="BiometricVerificationLog"/> row records was taken (E70/F01/US01/T01, NF-194, ADR-013-R18, R19). Stored as an integer: never
/// renumber; a new value goes at the end with its number.
/// </summary>
public enum IdentityDecisionMode
{
    /// <summary>The legacy path of E03/F04 and E22/F01 (the agent, or an operator); every row written before the column is of this kind.</summary>
    Legacy = 0,

    /// <summary>The self-hosted policy decided in observation: its outcome is only the shadow outcome of the row, and a person decides.</summary>
    Observation = 1,

    /// <summary>The self-hosted policy decided in a qualified segment with <c>Features:CheckInIdentityAutoApproval</c> on: its outcome is the outcome.</summary>
    Automatic = 2,
}
