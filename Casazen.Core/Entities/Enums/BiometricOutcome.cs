namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Face comparison outcome (NF-11, ADR-013-R5). The three levels come from the two configurable thresholds:
/// Accept (high confidence), Escalate (uncertain - operator verification), Reject (retry, then operator).
/// </summary>
public enum BiometricOutcome
{
    Accept,
    Escalate,
    Reject,
}
